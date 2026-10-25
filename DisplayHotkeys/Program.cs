using System.Runtime.InteropServices;
using System.Text.Json;

namespace DisplayHotkeys;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Stream Deck launches plugins with: -port N -pluginUUID X -registerEvent Y -info {...}
        int portIdx = Array.IndexOf(args, "-port");
        if (portIdx >= 0)
        {
            string Arg(string name) => args[Array.IndexOf(args, name) + 1];
            new StreamDeckPlugin().Run(int.Parse(Arg("-port")), Arg("-pluginUUID"), Arg("-registerEvent")).GetAwaiter().GetResult();
            return 0;
        }
        return Cli(args);
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    // Command line, for testing and for driving profiles from other tools.
    static int Cli(string[] args)
    {
        AttachConsole(-1);
        try
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case "--list":
                    foreach (var p in ProfileStore.LoadAll()) Console.WriteLine($"{p.Name}: {p.Describe()}");
                    return 0;
                case "--show":
                    foreach (var t in DisplayConfig.Capture("current").Targets) Console.WriteLine(t.Describe());
                    return 0;
                case "--save" when args.Length > 1:
                    var saved = DisplayConfig.Capture(args[1]);
                    ProfileStore.Save(saved);
                    Console.WriteLine($"Saved '{saved.Name}': {saved.Describe()}");
                    return 0;
                case "--apply" when args.Length > 1:
                    var profile = ProfileStore.Load(args[1]) ?? throw new InvalidOperationException($"No profile called '{args[1]}'.");
                    foreach (var w in DisplayConfig.Apply(profile)) Console.WriteLine("Warning: " + w);
                    Console.WriteLine($"Switched to '{profile.Name}'.");
                    return 0;
                case "--selftest":
                    // Capture the current layout, round-trip it through JSON and ask Windows to validate it (nothing changes on screen).
                    var json = JsonSerializer.Serialize(DisplayConfig.Capture("selftest"));
                    var copy = JsonSerializer.Deserialize<DisplayProfile>(json)!;
                    DisplayConfig.Apply(copy, validateOnly: true);
                    Console.WriteLine("OK: Windows accepted the captured layout:\n  " + string.Join("\n  ", copy.Targets.Select(t => t.Describe())));
                    return 0;
            }
            Console.WriteLine("This is a Stream Deck plugin. For testing: DisplayHotkeys [--list | --show | --save <name> | --apply <name> | --selftest]");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 2;
        }
    }
}
