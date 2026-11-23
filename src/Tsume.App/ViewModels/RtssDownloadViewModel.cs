namespace Tsume.App.ViewModels;

/// <summary>
/// The RTSS download page, which opens over the RTSS screen: automatic install, the Guru3D link,
/// and uninstalling. Everything it shows lives on <see cref="RtssViewModel"/>; this is only the layer.
/// </summary>
public sealed class RtssDownloadViewModel(Action opened, Action closed) : LayerViewModel
{
    protected override void OnOpened() => opened();

    protected override void OnClosed() => closed();
}
