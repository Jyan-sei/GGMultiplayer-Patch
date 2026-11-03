using System;
using System.Reflection;

namespace GunsawGenetics;

// one delegate, then a normal call. invoke boxes the arguments every time.
internal static class Calls
{
	internal static T Bind<T>(MethodInfo method) where T : Delegate
	{
		if (method == null)
			return null;
		try
		{
			return (T)method.CreateDelegate(typeof(T));
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] delegate " + method.DeclaringType + "." + method.Name + " failed: " + e.Message);
			return null;
		}
	}
}
