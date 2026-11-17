using System.Reflection;

namespace Tsume.App.Panel;

public partial class HeaderView
{
    public HeaderView() => InitializeComponent();

    /// <summary>"v1.0", or "v1.0.0.57" for a test build: the build's version without any commit suffix.</summary>
    public static string Version { get; } = "v" +
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];
}
