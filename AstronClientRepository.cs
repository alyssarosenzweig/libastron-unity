using UnityEngine;
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.IO;

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
		return new string(ReadChars(ReadUInt16()));
	}
}


public class AstronClientRepository {
	public bool m_connected = false;
	private string m_dcFile;

	private TcpClient socket;
	private NetworkStream stream;
	private BinaryWriter writer;
	private DatagramOut odgram;

	private AstronStream sout;

	private void initWithDC(string dcFile) {
		m_connected = false;
		m_dcFile = dcFile;
	}

	public AstronClientRepository(string dcFile) {
		initWithDC(dcFile);
	}

	public AstronClientRepository(string dcFile, string host, int port) {
		initWithDC(dcFile);
		connect(host, port);
	}

	public bool connect(string host, int port) {
		socket = new TcpClient();

		try {
			socket.Connect(host, port);
		} catch(SocketException e) {
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
		
}