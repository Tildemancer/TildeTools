using System;
using System.IO;
using System.Reflection;
using Dalamud.Interface.ImGuiNotification;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace TildeTools.Modules;

// A hosted plugin's settings, in a file of its own: asking Dalamud, it would get ours
internal sealed class HostedConfig<T>(string plugin, string path) where T : class, new()
{
    // Matches how Dalamud writes settings, so stored objects carry "$type"
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    // Binds to the running plugin's assembly, or the serializer loads a second copy of it
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Hosted = typeof(T).Assembly;
        private static readonly string HostedName = Hosted.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Whole name, not the hosted assembly first: a runtime generic can take its types as arguments
            var qualified = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";
            return Type.GetType(qualified, ResolveAssembly, ResolveType, throwOnError: false) ?? base.BindToType(assemblyName, typeName);
        }

        private static Assembly? ResolveAssembly(AssemblyName name) => name.Name == HostedName ? Hosted : Assembly.Load(name);

        private static Type? ResolveType(Assembly? assembly, string name, bool ignoreCase) =>
            assembly == null
                ? Type.GetType(name, throwOnError: false, ignoreCase)
                : assembly.GetType(name, throwOnError: false, ignoreCase);
    }

    // Settings existed but couldn't be read, so saving is refused and defaults never overwrite them
    private bool _loadFailed;

    internal T Load()
    {
        _loadFailed = false;

        try
        {
            if (!File.Exists(path))
                return new T();

            return Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("It read as empty.");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not read {plugin}'s settings at {path}. Running on defaults, and the file won't be overwritten.");
        }

        _loadFailed = true;
        Svc.Notifications.AddNotification(new()
        {
            Title = plugin,
            Content = $"Couldn't read its settings, so none are saved until {path} is fixed or removed.",
            Type = NotificationType.Warning,
        });

        return new T();
    }

    // False when refused or failed, so the save isn't reported done
    internal bool Save(T config)
    {
        if (_loadFailed)
        {
            Svc.Log.Warning($"Refusing to save {plugin}'s settings: the existing ones could not be read, and writing now would replace them with defaults.");
            return false;
        }

        try
        {
            Write(path, config);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not save {plugin}'s settings.");
            return false;
        }
    }

    private static T? Parse(string text) => JsonConvert.DeserializeObject<T>(text, SerializerSettings);

    private static void Write(string path, T config)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(config, Formatting.Indented, SerializerSettings));

        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
    }
}
