# QuickPlayer Training Studio

Training Studio is a local x64 WinForms companion for building and training the
binary foreground-prop model used by compatible automatic RVM initializers.

## Workflow

1. Open or create a dataset folder.
2. Index one or more folders containing videos. Indexing is recursive.
3. Use **Random frame** and decide each candidate as **Accept + label**,
   **No foreground objects**, or **Reject**.
4. For positive frames, create stable prop objects and annotate with SAM2 points,
   a SAM2 box, polygon, brush, or eraser. Mouse wheel zooms and the middle mouse
   button pans. Drafts save automatically.
5. Optionally create a nine-frame burst. The anchor can seed SAM2 drafts for its
   neighbours, but every frame remains in the review queue until decided.
6. Train after the dashboard has a useful mixture of sources and negatives.
   Cancellation leaves `last.pth`; enable **Resume latest checkpoint** to continue.
7. Inspect **Open error review**, then install the package. Installation does not
   enable it.
8. In QuickPlayer Custom Show settings, open the RVM tab, enable prop augmentation,
   and select the installed model.

The dataset keeps source videos in place. Accepted frames and masks are stored
under `samples`, in-progress work under `drafts`, and training runs under `runs`.
`dataset.json` is schema-versioned and replaced atomically.

## Verification

From the repository root:

```powershell
dotnet build .\IStripperQuickPlayer.TrainingStudio\IStripperQuickPlayer.TrainingStudio.csproj -c Release -p:Platform=x64 --no-restore
.\IStripperQuickPlayer.TrainingStudio\bin\x64\Release\net10.0-windows10.0.19041.0\IStripperQuickPlayer.TrainingStudio.exe --verify-training-studio
```
