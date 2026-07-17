using IStripperQuickPlayer.WinUI.Core;

namespace IStripperQuickPlayer.WinUI.Services;

public sealed class CardDeleteService
{
    private readonly IstripperPaths _paths;

    public CardDeleteService(IstripperPaths paths)
    {
        _paths = paths;
    }

    public void DeleteLocalCardFolders(ModelCard card)
    {
        string cardFolder = _paths.FindCardFolder(card.Name);
        if (!string.IsNullOrWhiteSpace(cardFolder) && Directory.Exists(cardFolder))
        {
            Directory.Delete(cardFolder, true);
        }

        string metadataFolder = _paths.FindCardMetadataFolder(card.Name);
        if (!string.IsNullOrWhiteSpace(metadataFolder) && Directory.Exists(metadataFolder))
        {
            Directory.Delete(metadataFolder, true);
        }
    }
}
