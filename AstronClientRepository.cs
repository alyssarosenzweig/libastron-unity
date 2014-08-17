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

enum MessageTypes : ushort {
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
	public bool m_connected = false;

	private TcpClient socket;
	private NetworkStream stream;
	private BinaryWriter writer;
	private DatagramOut odgram;

	private AstronStream sout;

	public Dictionary<UInt32, DistributedObject> doId2do = new Dictionary<UInt32, DistributedObject>();
	public Dictionary<UInt32, Interest> context2interest = new Dictionary<UInt32, Interest>();	

	public Action onHello;
	public Action<Interest> onAddInterest;

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

		m_connected = true;

		stream = socket.GetStream();
		writer = new BinaryWriter(stream);

		sout = new AstronStream();
		odgram = new DatagramOut(sout);

		beginReceiveData();

		return m_connected;
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

	public void unserializeType(DatagramIn dg, string type_n) {
		readPrimitive(dg, type_n);
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
	