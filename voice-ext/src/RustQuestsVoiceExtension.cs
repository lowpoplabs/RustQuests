using Oxide.Core;
using Oxide.Core.Extensions;

namespace Oxide.Ext.RustQuestsVoice
{
    /// <summary>
    /// Oxide extension host for the RustQuests voice pipeline. The extension exists so the
    /// unsandboxed work (binary HTTP, PCM math, vorbis encoding) can live in a plain DLL that
    /// any hosted server installs by dropping it into RustDedicated_Data/Managed — the same
    /// distribution path as Oxide.Ext.Discord. Plugins call VoiceTts.Render(...).
    /// </summary>
    public class RustQuestsVoiceExtension : Extension
    {
        public RustQuestsVoiceExtension(ExtensionManager manager) : base(manager) { }

        public override string Name => "RustQuestsVoice";
        public override string Author => "LowPopLabs";
        public override VersionNumber Version => new VersionNumber(0, 3, 0);

        // Let the plugin compiler resolve us without server-side config.
        public override string[] WhitelistAssemblies { get; protected set; } =
            new[] { "Oxide.Ext.RustQuestsVoice" };
        public override string[] WhitelistNamespaces { get; protected set; } =
            new[] { "Oxide.Ext.RustQuestsVoice" };
    }
}
