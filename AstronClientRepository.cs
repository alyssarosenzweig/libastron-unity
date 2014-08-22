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

	public Dictionary<UInt32, IDistributedObject> doId2do = new Dictionary<UInt32, IDistributedObject>();
	public Dictionary<UInt32, IDistributedObject> ovId2ov = new Dictionary<UInt32, IDistributedObject>();
	public Dictionary<UInt32, Interest> context2interest = new Dictionary<UInt32, Interest>();	

	public Action onHello;
	public Action<UInt16, string> onEject;

	public Action<Interest> onAddInterest;
	public Action<Interest> onDoneInterest;

	private UInt32 interestContextCounter = 0;

	private Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

	public MemoryStream incomingData;
	public bool dataReady = false;

	public int heartbeatInterval = 0;
	public int heartbeatCounter = 0; // send the first heartbeat immediately

	public ClientState state = ClientState.PREHELLO;

	public AstronClientRepository() {

	}

	public AstronClientRepository(string host, int port) {
		connect(host, port);
	}

	public bool connect(string host, int port) {
		socket = new TcpClient();

		try {
			socket.Connect(host, port);
		} catch(SocketException e) {
			Debug.LogException(e);
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
				incomingData = new MemoryStream(message);
				dataReady = true;

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
		
			Interest newInterest = new Interest(context, interest_id, zone_id, parent_id);
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
		case MessageTypes.CLIENT_ENTER_OBJECT_REQUIRED_OTHER:
		{
			unserializeClass(reader, SerializationLevel.REQUIRED_BCAST, false, true);
			break;
		}
		case MessageTypes.CLIENT_ENTER_OBJECT_REQUIRED_OWNER:
		{
			unserializeClass(reader, SerializationLevel.REQUIRED_BCAST_OR_OWNRECV, true, false);
			break;
		}
		case MessageTypes.CLIENT_ENTER_OBJECT_REQUIRED_OTHER_OWNER:
		{
			unserializeClass(reader, SerializationLevel.REQUIRED_BCAST_OR_OWNRECV, true, true);
			break;
		}
		case MessageTypes.CLIENT_OBJECT_LOCATION:
		{
			UInt32 do_id = reader.ReadUInt32();
			UInt32 parent_id = reader.ReadUInt32();
			UInt32 zone_id = reader.ReadUInt32();

			doId2do[do_id].getLocation().changeLocation(zone_id, parent_id);
			doId2do[do_id].locationChanged();
			break;
		}
		case MessageTypes.CLIENT_OBJECT_LEAVING:
		{
			UInt32 doId = reader.ReadUInt32();
			doId2do[doId].leaving();

			// freeing the DO from the doId2do map is done by the leaving method
			// via the removeDOfromMap function

			// if the leaving method is overriden,
			// removeDOfromMap should still be called to prevent memory leaks
			break;
		}
		case MessageTypes.CLIENT_OBJECT_SET_FIELD:
		{
			UInt32 doId = reader.ReadUInt32();
			UInt16 field_id = reader.ReadUInt16();

			receiveUpdate(reader, doId2do[doId], field_id);
			break;
		}
		case MessageTypes.CLIENT_OBJECT_SET_FIELDS:
		{
			UInt32 doId = reader.ReadUInt32();

			IDistributedObject distObj = doId2do[doId];

			UInt16 num_fields = reader.ReadUInt16();
			for(int i = 0; i < num_fields; ++i) {
				UInt16 field_id = reader.ReadUInt16();
				receiveUpdate(reader, distObj, field_id);
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

	public void sendSuddenDisconnect() {
		odgram.Write((UInt16) MessageTypes.CLIENT_DISCONNECT);
		sout.Flush (writer);
		connected = false;
		socket.Close();
	}

	// TODO: once Astron issue #261 is resolved, call sendHeartbeat in a loop somehow

	public void sendHeartbeat() {
		odgram.Write ((UInt16) MessageTypes.CLIENT_HEARTBEAT);
		sout.Flush(writer);
	}

	public void sendUpdate(UInt32 doID, string methodName, object[] parameters) {
		if(!doId2do.ContainsKey(doID)) {
			Debug.Log ("ERROR: Attempt to call "+methodName+" on unknown DO "+doID);
			return;
		}

		IDistributedObject distObj;
		doId2do.TryGetValue(doID, out distObj);

		UInt16 fieldID;
		DCFile.reverseFieldLookup.TryGetValue(distObj.getClass()+"::"+methodName, out fieldID);

		odgram.Write ((UInt16) MessageTypes.CLIENT_OBJECT_SET_FIELD);
		odgram.Write (doID);
		odgram.Write (fieldID);

		string[] parametersTypes = DCFile.fieldLookup[fieldID];
		for(int i = 0; i < parametersTypes.Length; ++i) {
			serializeType(odgram, parametersTypes[i], parameters[i]);
		}
		sout.Flush(writer);
	}

	public UInt32 getInterestContext() {
		return interestContextCounter++;
	}

	public Interest addInterest(Location loc) {
		UInt32 context = getInterestContext();
		UInt16 interestID = 0;

		Interest i = new Interest(context, interestID, loc.getParentId(), loc.getZone());
		context2interest.Add(context, i);

		odgram.Write ((UInt16) MessageTypes.CLIENT_ADD_INTEREST);
		odgram.Write (context);
		odgram.Write (interestID);
		odgram.Write (loc.getParentId());
		odgram.Write (loc.getZone());
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
		case "float64":
			return dg.ReadDouble();
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
			dg.Write( Convert.ToInt16(value)); // bad bad bad shadow TODO: properly implement floating types and nuke OTP's math
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
		case "float64":
			dg.Write ( (double) value);
			break;
		default:
			Debug.Log ("Writing Error: Type '"+type_n+"' is not a primitive");
			break;
		}
	}

	public void serializeType(DatagramOut dg, string type_n, object value) {
		if(type_n.Contains("int")) {
			int divideBy = 1;
			
			if(type_n.Contains("/")) {
				string[] divideParts = type_n.Split("/".ToCharArray());
				
				divideBy = Int32.Parse(divideParts[1]);
				
				type_n = divideParts[0];
			}
			
			if(divideBy != 1) {
				writePrimitive(dg, type_n, (Int64) Convert.ToInt64(((double)value) * divideBy));
				return;
			}
			
		}
		
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

		IDistributedObject distObj;

		if(t.IsSubclassOf(typeof(DistributedUnityObject))) {
			Debug.Log ("DistributedUnityObject instantiated of type "+t);

			GameObject prefab;

			try {
				prefab = prefabs[r_class_n];
			} catch(Exception e) {
				Debug.LogException(e);
				return;
			}

			GameObject gameObject = UnityEngine.Object.Instantiate(prefab) as GameObject;

			distObj = gameObject.GetComponent<MonoBehaviour>() as IDistributedObject;
		} else {
			Debug.Log ("DistributedObject instantiated of type "+t);

			distObj = Activator.CreateInstance(t) as IDistributedObject;
		}

		distObj.setCR(this);

		// give it some context

		distObj.setDoID(do_id);
		distObj.setLocation(new Location(zone_id, parent_id));

		// to unpack required fields, first get a list of all fields

		UInt16[] field_list = DCFile.classLookup[class_n];
	

		// next, iterate through the fields to find required fields

		for(int i = 0; i < field_list.Length; ++i) {
			string[] modifiers = DCFile.fieldModifierLookup[field_list[i]];

			if(Array.IndexOf(modifiers, "required") > -1) {
				if(level == SerializationLevel.REQUIRED) {
					// go ahead, receive the update
					receiveUpdate(dg, distObj, field_list[i]);
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

		if(owner) {
			ovId2ov.Add (do_id, distObj);
		} else {
			doId2do.Add(do_id, distObj);
		}
	}

	public void receiveUpdate(DatagramIn dg, IDistributedObject distObj, UInt16 field_id) {
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

	public void prefab(string name, GameObject _prefab) {
		prefabs.Add (name, _prefab);
	}

	public void loop() {
		if(dataReady) {
			dataReady = false;
			onData(incomingData); // Unity requires game code to run in a single thread
								  // However, async reading spawns a seperate thread
									// loop should be called from Update() in the main thread
			beginReceiveData();
		}

		if(heartbeatInterval != 0 && heartbeatCounter-- <= 0) {
			sendHeartbeat(); // let the server know we are alive
			heartbeatCounter = heartbeatInterval;
		}
	}

	public void removeDOfromMap(UInt32 doId) {
		doId2do.Remove(doId);
	}

}

public interface IDistributedObject {
	void setCR(AstronClientRepository cr);
	void sendUpdate(string methodName, params object[] parameters);
	string getClass();

	UInt32 getDoID();
	void setDoID(UInt32 doId);

	Location getLocation();
	void setLocation(Location loc);
	void locationChanged();

	void leaving();
}

public class DistributedObject : IDistributedObject {
	public UInt32 doID = 0;
	private Location my_location;

	protected AstronClientRepository cr;

	public DistributedObject() {}

	public void setCR(AstronClientRepository _cr) {
		cr = _cr;
	}

	public void sendUpdate(string methodName, params object[] parameters) {
		try {
			cr.sendUpdate(doID, methodName, parameters);
		} catch(Exception e) {
			Debug.LogException(e);
		}
	}

	public string getClass() {
		return this.GetType().Name; // while this seems like utter nonsense, it allows the ACR to perform reflection on subclasses of DOs
	}

	public UInt32 getDoID() {
		return doID;
	}

	public void setDoID(UInt32 _doID) {
		doID = _doID;
	}

	public static bool isUnityObject() {
		return false;
	}

	public virtual void leaving() {
		Debug.Log (getClass()+"("+getDoID()+") leaving");
		cr.removeDOfromMap(doID);
	}

	public Location getLocation() {
		return my_location;
	}

	public void setLocation(Location loc) {
		my_location = loc;
	}

	public virtual void locationChanged() { } // subclasses should override this method
}

public class DistributedUnityObject : MonoBehaviour, IDistributedObject {
	public UInt32 doID = 0;
	protected AstronClientRepository cr;
	private Location my_location;

	public static UnityEngine.Object prefab;

	public void setCR(AstronClientRepository _cr) {
		cr = _cr;
	}

	public void sendUpdate(string methodName, params object[] parameters) {
		try {
			cr.sendUpdate(doID, methodName, parameters);
		} catch(Exception e) {
			Debug.LogException(e);
		}
	}

	public string getClass() {
		return this.GetType().Name;
	}

	public UInt32 getDoID() {
		return doID;
	}

	public void setDoID(UInt32 _doID) {
		doID = _doID;
	}

	public static bool isUnityObject() {
		return true;
	}

	public virtual void leaving() {
		Debug.Log (getClass()+"("+getDoID()+") leaving");
		Destroy (gameObject);
		cr.removeDOfromMap(doID);
	}

	public Location getLocation() {
		return my_location;
	}
	
	public void setLocation(Location loc) {
		my_location = loc;
	}

	public virtual void locationChanged() { } // subclasses should override this method
}

public class DistributedObjectOV : DistributedObject {
	public IDistributedObject normalView() {
		IDistributedObject o;

		cr.doId2do.TryGetValue(getDoID(), out o);

		return o;
	}
}

public class Location {
	private UInt32 zone;
	private UInt32 parent_id;

	public Location(UInt32 _zone, UInt32 _parent_id) {
		zone = _zone;
		parent_id = _parent_id;
	}

	public UInt32 getZone() {
		return zone;
	}

	public UInt32 getParentId() {
		return parent_id;
	}

	public void changeLocation(UInt32 newZone, UInt32 newParentId) {
		zone = newZone;
		parent_id = newParentId;
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
	