using System.Collections;
using System.Net;
using System.Net.Sockets;

/*
 * AstronClientRepository.cs
 * Copyright (C) bobbybee, shadowcoder, and the Astron team 2014
 * C# implementation of the Astron multiplayer client
 * See https://github.com/Astron/Astron for more information
 * */

public class AstronClientRepository {
	public bool m_connected = false;
	private string m_dcFile;

	private TcpClient socket;

	public AstronClientRepository(string dcFile) {
		m_connected = false;
		m_dcFile = dcFile;
	}

	public AstronClientRepository(string dcFile, string host, int port) {
		AstronClientRepository();
		connect(host, port);
	}

	public bool connect(string host, int port) {
		try {
			socket.Connect(host, port);
		} catch(SocketException e) {
			return false;
		}

		m_connected = true;

		return m_connected;
	}

		
}
