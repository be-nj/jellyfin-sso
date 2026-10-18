using Xunit;

// Plugin.Instance is a static singleton, so tests that stand up a plugin instance would
// race each other. The suite is small; running it serially is cheaper than threading a
// configuration accessor through the plugin.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
