using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;
using Together;

namespace GunsawGenetics;

// Put and the send methods take spans. net48 would need System.Memory to call them direct.
internal static class NetIo
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ServerSend(this NetDataWriter writer, IReadOnlyCollection<knetid> ids)
	{
		Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ids);
	}
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ServerSend(this NetDataWriter writer, knetid id)
	{
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, id);
    }
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ClientSend(this NetDataWriter writer)
	{
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }
}
