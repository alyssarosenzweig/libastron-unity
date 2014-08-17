using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Reflection;

/*
 * AstronClientRepository.cs
 * Copyright (C) bobbybee, shadowcoder, and the Astron team 2014
 * C# implementation of the Astron multiplayer client
 * See https://github.com/Astron/Astron for more information
 * */

public enum SerializationLevel {
	REQUIRED,
	REQUIRED_BCAST,
	REQUIRED_BCAST_OR_OWNRECV
};

public enum MessageTypes : ushort {
	CLIENT_HELLO = 1,
	CLIENT_HELLO_RESP = 2,
	CLIENT_DISCONNECT = 3,
	CLIENT_EJECT = 4,
	CLIENT_HEARTBEAT = 5,
	CLIENT_OBJECT_SET_FIELD = 120,
	CLIENT_OBJECT_SET_FIELDS = 121,
	CLIENT_OBJECT_LEAVING = 132,
	CLIENT_OBJECT_LEAVING_OWNER = 161,
	CLIENT_OBJECT_LOCATION = 140,
	CLIENT_ENTER_OBJECT_REQUIRED = 142,
	CLIENT_ENTER_OBJECT_REQUIRED_OTHER = 143,
	CLIENT_ENTER_OBJECT_REQUIRED_OWNER = 172,
	CLIENT_ENTER_OBJECT_REQUIRED_OTHER_OWNER = 173,
	CLIENT_DONE_INTEREST_RESP = 204,
	CLIENT_ADD_INTEREST = 200,
	CLIENT_ADD_INTEREST_MULTIPLE = 201,
	CLIENT_REMOVE_INTEREST = 203
};

public class AstronStream : MemoryStream {
	public void Flush(BinaryWriter o) {
		o.Write( (UInt16) Length);

		byte[] payload = ToArray();

		o.Write(payload);
		Seek (0, SeekOrigin.Begin);
		SetLength (0);
	}

}

public class DatagramOut : BinaryWriter {
	public DatagramOut(AstronStream s) : base(s) {

	}

	public override void Write(string s) {
		Write( (UInt16) s.Length );

		Write( s.ToCharArray() );
	}
}

public class DatagramIn : BinaryReader {
	public DatagramIn(Stream s) : base(s) {}

	public override string ReadString() {
		return System.Text.Encoding.Default.GetString(ReadBlob());
	}

	public byte[] ReadBlob() {
		return ReadBytes(ReadUInt16());
	}
}

public class AstronClientRepository {
	public bool connected = false;

	private TcpClient socket;
	private NetworkStream stream;
	private BinaryWriter writer;
	private DatagramOut odgram;

	private AstronStream sout;

	public Dictionary<UInt32, DistributedObject> doId2do = new Dictionary<UInt32, DistributedObject>();
	public Dictionary<UInt32, Interest> context2interest = new Dictionary<UInt32, Interest>();	

	public Action onHello;
	public Action onSuddenDisconnect;
	public Action<UInt16, string> onEject;

	public Action<Interest> onAddInterest;
	public Action<Interest> onDoneInterest;

	private UInt32 interestContextCounter = 0;

	public AstronClientRepository() {

	}

	public AstronClientRepository(string host, int port) {
		connect(host, port);
	}

	public bool connect(string host, int port) {
		socket = new TcpClient();

		try {
			socket.Connect(host, port);
		} catch(SocketException /*e*/) {
			return false;
		}

		connected = true;

		stream = socket.GetStream();
		writer = new BinaryWriter(stream);

		sout = new AstronStream();
		odgram = new DatagramOut(sout);

		beginReceiveData();

		return connected;
	}

	private void beginReceiveData() {
		byte[] sizeBuf = new byte[2];
	

		stream.BeginRead(sizeBuf, 0, 2, (asyncResult) =>
		{
			int size = (sizeBuf[1] << 8) | sizeBuf[0];

			byte[] message = new byte[size];

			stream.BeginRead (message, 0, size, (asyncResult2) =>
			{
				onData(new MemoryStream(message));

				beginReceiveData();
			}, stream);
		}, stream);
	}

	private void onData(MemoryStream data) {
		DatagramIn reader = new DatagramIn(data);

		UInt16 type = reader.ReadUInt16();

		switch( (MessageTypes) type) {
		
		case MessageTypes.CLIENT_HELLO_RESP:
		{
			Debug.Log ("Response to client_hello");

			if(onHello != null) {
				onHello();
			}
			break;
		}
		case MessageTypes.CLIENT_DISCONNECT:
		{
			Debug.Log ("Sudden disconnect");

			if(onSuddenDisconnect != null) {
				onSuddenDisconnect();
			}
			break;
		}
		case MessageTypes.CLIENT_EJECT:
		{
			UInt16 error_code = reader.ReadUInt16();
			string reason = reader.ReadString();

			Debug.Log ("Ejected Code "+error_code+": "+reason);

			if(onEject != null) {
				onEject(error_code, reason);
			}
			break;
		}
		case MessageTypes.CLIENT_ADD_INTEREST:
		{
			UInt32 context = reader.ReadUInt32();
			UInt16 interest_id = reader.ReadUInt16();
			UInt32 parent_id = reader.ReadUInt32();
			UInt32 zone_id = reader.ReadUInt32();

			Interest newInterest = new Interest(context, interest_id, parent_id, zone_id);
			context2interest.Add(context, newInterest);

			if(onAddInterest != null) {
				onAddInterest(newInterest);
			}

			break;
		}
		case MessageTypes.CLIENT_DONE_INTEREST_RESP:
		{
			UInt32 context = reader.ReadUInt32 ();
			UInt16 interest_id = reader.ReadUInt16();

			if(onDoneInterest != null) {
				onDoneInterest(context2interest[context]);
			}

			break;
		}
		case MessageTypes.CLIENT_ENTER_OBJECT_REQUIRED:
		{
			unserializeClass(reader, SerializationLevel.REQUIRED_BCAST, false, false);
			break;
		}
		case MessageTypes.CLIENT_ENTER_OBJECT_REQUIRED_OWNER:
		{
			unserializeClass(reader, SerializationLevel.REQUIRED_BCAST_OR_OWNRECV, true, false);
			break;
		}
		default:
		{
			Debug.Log ("Unknown message type: " + type);
			break;
		}
		
		}
	}

	public void sendClientHello(string version, UInt32 dcHash) {
		odgram.Write((UInt16) MessageTypes.CLIENT_HELLO);
		odgram.Write(dcHash);
		odgram.Write(version);
		sout.Flush(writer);
	}

	public void sendUpdate(UInt32 doID, string methodName, object[] parameters) {
		if(!doId2do.ContainsKey(doID)) {
			Debug.Log ("ERROR: Attempt to call "+methodName+" on unknown DO "+doID);
			return;
		}

		DistributedObject distObj;
		doId2do.TryGetValue(doID, out distObj);

		UInt16 fieldID;
		DCFile.reverseFieldLookup.TryGetValue(distObj.getClass()+"::"+methodName, out fieldID);

		odgram.Write ((UInt16) MessageTypes.CLIENT_OBJECT_SET_FIELD);
		odgram.Write (doID);
		odgram.Write (fieldID);

		string[] parametersTypes = DCFile.fieldLookup[fieldID];
		for(int i = 0; i < parametersTypes.Length; ++i) {
			Debug.Log(parametersTypes[i]+" "+parameters[i]);

			serializeType(odgram, parametersTypes[i], parameters[i]);
		}
		sout.Flush(writer);
	}

	public UInt32 getInterestContext() {
		return interestContextCounter++;
	}

	public Interest addInterest(UInt32 parentID, UInt32 zoneID) {
		UInt32 context = getInterestContext();
		UInt16 interestID = 0;

		Interest i = new Interest(context, interestID, parentID, zoneID);
		context2interest.Add(context, i);

		odgram.Write ((UInt16) MessageTypes.CLIENT_ADD_INTEREST);
		odgram.Write (context);
		odgram.Write (interestID);
		odgram.Write (parentID);
		odgram.Write (zoneID);
		sout.Flush (writer);

		return i;
	}

	public void removeInterest(Interest i) {
		odgram.Write ((UInt16) MessageTypes.CLIENT_REMOVE_INTEREST);
		odgram.Write (i.getContext());
		odgram.Write (i.getID());
		sout.Flush(writer);
	}

	// read/write *primitive* types from an Astron stream (e.g.: uint16)
	// distinguished from a higher level type, such as structs, dclasses,
	// or uint16%360/100, all of which are made up of primitive types

	public object readPrimitive(DatagramIn dg, string type_n) {
		switch(type_n) {
		case "uint8":
			return dg.ReadByte();
		case "uint16":
			return dg.ReadUInt16();
		case "uint32":
			return dg.ReadUInt32();
		case "uint64":
			return dg.ReadUInt64();
		case "int8":
			return dg.ReadSByte();
		case "int16":
			return dg.ReadInt16();
		case "int32":
			return dg.ReadInt32();
		case "int64":
			return dg.ReadInt64();
		case "string":
			return dg.ReadString();
		case "blob":
			return dg.ReadBlob();
		default:
			Debug.Log ("Reading Error: Type '"+type_n+"' is not a primitive");
			return null;
		}
	}

	public void writePrimitive(DatagramOut dg, string type_n, object value) {
		switch(type_n) {
		case "uint8":
			dg.Write( (Byte) value);
			break;
		case "uint16":
			dg.Write( (UInt16) value);
			break;
		case "uint32":
			dg.Write( (UInt32) value);
			break;
		case "uint64":
			dg.Write( (UInt64) value);
			break;
		case "int8":
			dg.Write( (SByte) value);
			break;
		case "int16":
			dg.Write( (Int16) value);
			break;
		case "int32":
			dg.Write( (Int32) value);
			break;
		case "int64":
			dg.Write( (Int64) value);
			break;
		case "string":
			dg.Write( (String) value);
			break;
		case "blob":
			dg.Write( (byte[]) value);
			break;
		default:
			Debug.Log ("Writing Error: Type '"+type_n+"' is not a primitive");
			break;
		}
	}

	public void serializeType(DatagramOut dg, string type_n, object value) {
		// TODO: actually support complex types

		writePrimitive(dg, type_n, value);
	}

	public object unserializeType(DatagramIn dg, string type_n) {
		if(type_n.Contains("int")) {
			int divideBy = 1;

			if(type_n.Contains("/")) {
				string[] divideParts = type_n.Split("/".ToCharArray());
				divideBy = Int32.Parse(divideParts[1]);

				type_n = divideParts[0];
			}

			if(divideBy != 1) {
				// run unoptimized


				object originalPrim = readPrimitive(dg, type_n);


				double prim = System.Convert.ToDouble(originalPrim);


				return prim / divideBy; 
			}

		}

		return readPrimitive(dg, type_n);
	}

	public void unserializeClass(DatagramIn dg, SerializationLevel level, bool owner, bool optionals) {
		// since it's the same for all the messages, quickly unpack the header

		UInt32 do_id = dg.ReadUInt32();
		UInt32 parent_id = dg.ReadUInt32();
		UInt32 zone_id = dg.ReadUInt32();
		UInt16 dclass_id = dg.ReadUInt16();

		string class_n = DCFile.DCRoot[dclass_id];

		// when unpacking a class, there are two phases:
		// 1) unpacking the required fields (required modifiers are defined by level)
		// and 2) unpacking optional fields

		string r_class_n = owner ? class_n + "OV" : class_n;

		Type t = Type.GetType(r_class_n);

		DistributedObject distObj;

		try {
			distObj = Activator.CreateInstance(t, this) as DistributedObject;
		} catch(Exception e) {
			Debug.Log (e);
			return;
		}

		// give it some context

		distObj.doID = do_id;

		// to unpack required fields, first get a list of all fields

		UInt16[] field_list = DCFile.classLookup[class_n];
	

		// next, iterate through the fields to find required fields

		for(int i = 0; i < field_list.Length; ++i) {
			string[] modifiers = DCFile.fieldModifierLookup[field_list[i]];

			Debug.Log (modifiers);

			if(Array.IndexOf(modifiers, "required") > -1) {
				if(level == SerializationLevel.REQUIRED) {
					// go ahead, receive the update
					receiveUpdate(dg,  distObj, field_list[i]);
				} else if(level == SerializationLevel.REQUIRED_BCAST) {
					// only if it contains broadcast
					if(Array.IndexOf(modifiers, "broadcast") > -1) {
						receiveUpdate(dg, distObj, field_list[i]);
					}
				} else if(level == SerializationLevel.REQUIRED_BCAST_OR_OWNRECV) {
					// it either needs to contain broadcast OR ownrecv
					if( (Array.IndexOf(modifiers, "broadcast") > -1) || (Array.IndexOf(modifiers, "ownrecv") > -1)) {
						receiveUpdate(dg, distObj, field_list[i]);
					}
				}
			}
		}

		// without optionals, we'd be done
		// however, unpacking optionals is significantly easier
		// because we don't care about modifiers.
		// assume the server is sending fields we understand

		if(optionals) {
			UInt16 numOptionals = dg.ReadUInt16();

			for(int o = 0; o < numOptionals; ++o) {
				receiveUpdate(dg, distObj, dg.ReadUInt16());
			}
		}

		doId2do.Add(do_id, distObj);
	}

	public void receiveUpdate(DatagramIn dg, DistributedObject distObj, UInt16 field_id) {
		Debug.Log("Receive update for class "+distObj.getClass()+" field ID"+field_id);

		// first get the array of fields we'll need to unpack

		string[] t_fields = DCFile.fieldLookup[field_id];

		// next, define an empty array of params the size of the number of fields
		object[] t_params = new object[t_fields.Length];

		// unpack the parameters

		for(int i = 0; i < t_fields.Length; ++i) {
			t_params[i] = unserializeType(dg, t_fields[i]);
		}

		// finally, use reflection to call the function

		Type t_type = distObj.GetType();
		MethodInfo t_method = t_type.GetMethod(DCFile.fieldNameLookup[field_id]);
		t_method.Invoke(distObj, t_params);
	}

}

public class DistributedObject {
	public UInt32 doID = 0;

	private AstronClientRepository cr;

	public DistributedObject(AstronClientRepository _cr) {
		cr = _cr;
	}

	public void legacyUpdate(string methodName, object[] parameters) {
		cr.sendUpdate(doID, methodName, parameters); // pass off to ACR to do the dirty work
	}

	public void sendUpdate(string methodName, params string[] parameters) {
	
		Debug.Log (parameters.Length);
		cr.sendUpdate(doID, methodName, parameters);
	}

	public string getClass() {
		return this.GetType().Name; // while this seems like utter nonsense, it allows the ACR to perform reflection on subclasses of DOs
	}
}

public class DistributedObjectOV : DistributedObject {
	public DistributedObjectOV(AstronClientRepository cr) : base(cr) {

	}
}

public class Interest {
	private UInt32 context;
	private UInt16 interest_id;
	private UInt32 parent_id;
	private UInt32[] zones;

	public Interest(UInt32 _context, UInt16 _interest_id, UInt32 _parent_id, UInt32[] _zones) {
		context = _context;
		interest_id = _interest_id;
		parent_id = _parent_id;
		zones = _zones;
	}

	public Interest(UInt32 _context, UInt16 _interest_id, UInt32 _parent_id, UInt32 _zone)
		: this(_context, _interest_id, _parent_id, new UInt32[]{_zone})
	{
		// pass
	}

	public UInt32 getZone() {
		return zones[0];
	}

	public UInt32[] getZones() {
		return zones;
	}

	public UInt32 getParentID() {
		return parent_id;
	}

	public UInt16 getID() {
		return interest_id;
	}

	public UInt32 getContext() {
		return context;
	}
}
	