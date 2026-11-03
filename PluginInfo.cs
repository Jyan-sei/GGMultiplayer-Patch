namespace GunsawGenetics;

internal static class PluginInfo
{
	// same guid as the stock 1.0.0 bridge. 2.0.0 wins the chainloader version check.
	// has to be a real System.Version.
	internal const string GUID = "gunsawgeneticsmpbridge";
	internal const string Name = "Gunsaw Genetics MP Bridge";
	internal const string Version = "2.0.0";

	// the assembly name gg asks for. our file is ggmpcompat.dll.
	internal const string StockAssemblyName = "GunsawGeneticsMPBridge";
}
