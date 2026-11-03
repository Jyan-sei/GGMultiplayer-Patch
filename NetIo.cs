using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;
using Together;

namespace GunsawGenetics;

// Put and the send methods take spans. net48 would need System.Memory to call them direct.
internal static class NetIo
{
	delegate void PutStr(NetDataWriter writer, string value, int maxLength);
	delegate void PutIntDel(NetDataWriter writer, int value);
	delegate void PutUShortDel(NetDataWriter writer, ushort value);
	delegate void PutBoolDel(NetDataWriter writer, bool value);
	delegate void PutFloatDel(NetDataWriter writer, float value);
	delegate void PutByteDel(NetDataWriter writer, byte value);
	delegate void SendWriter(in DeliveryMethod delivery, in NetDataWriter writer);
	delegate void SendMany(in DeliveryMethod delivery, in NetDataWriter writer, in IEnumerable<knetid> clients);
	delegate void SendOne(in DeliveryMethod delivery, in NetDataWriter writer, in knetid client);

	// Put(string) is gone. the live one is Put(string, int maxLength).
	static readonly PutStr PutString = Calls.Bind<PutStr>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(string), typeof(int) }));
	static readonly PutIntDel PutInt = Calls.Bind<PutIntDel>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(int) }));
	static readonly PutUShortDel PutUShort = Calls.Bind<PutUShortDel>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(ushort) }));
	static readonly PutBoolDel PutBool = Calls.Bind<PutBoolDel>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(bool) }));
	static readonly PutFloatDel PutFloat = Calls.Bind<PutFloatDel>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(float) }));
	static readonly PutByteDel PutByte = Calls.Bind<PutByteDel>(typeof(NetDataWriter).GetMethod("Put", new[] { typeof(byte) }));
	static readonly SendWriter ClientSendMethod = Calls.Bind<SendWriter>(FindClientSend());
	static readonly SendMany ServerSendEnumerable = Calls.Bind<SendMany>(FindServerSendEnumerable());
	static readonly SendOne ServerSendOne = Calls.Bind<SendOne>(FindServerSendOne());

	static MethodInfo FindClientSend()
	{
		foreach (var m in typeof(Net).GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (m.Name != "Client_Send")
				continue;
			var p = m.GetParameters();
			if (p.Length == 2 && p[1].ParameterType.Name.Contains("NetDataWriter"))
				return m;
		}
		return null;
	}

	static Type Bare(Type type)
	{
		return type != null && type.IsByRef ? type.GetElementType() : type;
	}

	static MethodInfo FindServerSendEnumerable()
	{
		foreach (var m in typeof(Net).GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (m.Name != "Server_SendToClients")
				continue;
			var p = m.GetParameters();
			if (p.Length != 3)
				continue;
			// in-parameters show up as byref, so the assignable check used to miss the real method
			if (Bare(p[1].ParameterType).Name.Contains("NetDataWriter")
			    && typeof(IEnumerable<knetid>).IsAssignableFrom(Bare(p[2].ParameterType)))
				return m;
		}
		return null;
	}

	static MethodInfo FindServerSendOne()
	{
		foreach (var m in typeof(Net).GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (m.Name != "Server_SendToClients")
				continue;
			var p = m.GetParameters();
			if (p.Length == 3 && Bare(p[1].ParameterType).Name.Contains("NetDataWriter")
			    && Bare(p[2].ParameterType) == typeof(knetid))
				return m;
		}
		return null;
	}

	internal static void Put(NetDataWriter writer, string value)
	{
		if (PutString == null)
			throw new MissingMethodException("NetDataWriter.Put(string, int)");
		PutString(writer, value ?? "", 0);
	}

	internal static void Put(NetDataWriter writer, int value)
	{
		PutInt(writer, value);
	}

	internal static void Put(NetDataWriter writer, ushort value)
	{
		PutUShort(writer, value);
	}

	internal static void Put(NetDataWriter writer, bool value)
	{
		PutBool(writer, value);
	}

	internal static void Put(NetDataWriter writer, float value)
	{
		PutFloat(writer, value);
	}

	internal static void Put(NetDataWriter writer, byte value)
	{
		PutByte(writer, value);
	}

	internal static void ClientSend(NetDataWriter writer)
	{
		if (ClientSendMethod != null)
			ClientSendMethod(DeliveryMethod.ReliableOrdered, writer);
	}

	internal static void ServerSend(NetDataWriter writer, IEnumerable<knetid> clients)
	{
		if (ServerSendEnumerable == null)
			throw new MissingMethodException("Net.Server_SendToClients(writer, clients)");
		ServerSendEnumerable(DeliveryMethod.ReliableOrdered, writer, clients);
	}

	internal static void ServerSend(NetDataWriter writer, knetid client)
	{
		if (ServerSendOne != null)
			ServerSendOne(DeliveryMethod.ReliableOrdered, writer, client);
	}
}
