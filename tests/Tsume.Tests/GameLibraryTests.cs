using Tsume.Upscaling.Games;

namespace Tsume.Tests;

public sealed class GameLibraryTests
{
    [Fact]
    public void CustomFromProgram_UsesMatchingStoreNameAndRootFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Tsume-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gameRoot = Path.Combine(root, "BlackMythWukong");
            var exe = Path.Combine(gameRoot, "CustomSub", "b1-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, []);

            var storeGame = Game.At(gameRoot, "Black Myth: Wukong", GameStore.Steam);
            var custom = GameLibrary.CustomFromProgram(exe, [storeGame]);

            Assert.Equal("Black Myth: Wukong", custom.Name);
            Assert.Equal(storeGame.Folder, custom.Folder);
            Assert.Equal(storeGame.Id, custom.Id);
            Assert.Equal(GameStore.Custom, custom.Store);
            Assert.Equal(exe, custom.Executable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomFromProgram_PrefersDeepestMatchingKnownGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "Tsume-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var outer = Path.Combine(root, "Suite");
            var inner = Path.Combine(outer, "InnerGame");
            var exe = Path.Combine(inner, "game.exe");
            Directory.CreateDirectory(inner);
            File.WriteAllBytes(exe, []);

            var outerGame = Game.At(outer, "Outer Suite", GameStore.Folder);
            var innerGame = Game.At(inner, "Inner Game Title", GameStore.Epic);
            var custom = GameLibrary.CustomFromProgram(exe, [outerGame, innerGame]);

            Assert.Equal("Inner Game Title", custom.Name);
            Assert.Equal(innerGame.Folder, custom.Folder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomFromProgram_UsesInferredRootFolderNameForUnrealAndBinLayouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Tsume-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ueRoot = Path.Combine(root, "Clair Obscur Expedition 33");
            var ueBin = Path.Combine(ueRoot, "Sandfall", "Binaries", "Win64");
            Directory.CreateDirectory(Path.Combine(ueRoot, "Engine"));
            Directory.CreateDirectory(ueBin);
            var ueExe = Path.Combine(ueBin, "Sandfall-Win64-Shipping.exe");
            File.WriteAllBytes(ueExe, []);

            var ueGame = GameLibrary.CustomFromProgram(ueExe, Array.Empty<Game>());
            Assert.Equal("Clair Obscur Expedition 33", ueGame.Name);
            Assert.Equal(ueRoot, ueGame.Folder);

            var binRoot = Path.Combine(root, "Cyberpunk 2077");
            var binX64 = Path.Combine(binRoot, "bin", "x64");
            Directory.CreateDirectory(binX64);
            var binExe = Path.Combine(binX64, "Cyberpunk2077.exe");
            File.WriteAllBytes(binExe, []);

            var binGame = GameLibrary.CustomFromProgram(binExe, Array.Empty<Game>());
            Assert.Equal("Cyberpunk 2077", binGame.Name);
            Assert.Equal(binRoot, binGame.Folder);

            var singleBinRoot = Path.Combine(root, "Control Ultimate Edition");
            var singleBin = Path.Combine(singleBinRoot, "bin");
            Directory.CreateDirectory(singleBin);
            var singleBinExe = Path.Combine(singleBin, "Control_DX12.exe");
            File.WriteAllBytes(singleBinExe, []);

            var singleBinGame = GameLibrary.CustomFromProgram(singleBinExe, Array.Empty<Game>());
            Assert.Equal("Control Ultimate Edition", singleBinGame.Name);
            Assert.Equal(singleBinRoot, singleBinGame.Folder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomFromProgram_UsesParentFolderNameForGdkContentFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Tsume-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var content = Path.Combine(root, "Forza Horizon 5", "Content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), "<Game />");
            var exe = Path.Combine(content, "ForzaHorizon5.exe");
            File.WriteAllBytes(exe, []);

            var custom = GameLibrary.CustomFromProgram(exe, Array.Empty<Game>());
            Assert.Equal("Forza Horizon 5", custom.Name);
            Assert.Equal(content, custom.Folder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomFromProgram_FallsBackToExecutableNameAtDriveRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var exe = Path.Combine(root, "Witchfire-Win64-Shipping.exe");

        var custom = GameLibrary.CustomFromProgram(exe, Array.Empty<Game>());
        Assert.Equal("Witchfire", custom.Name);
    }
}
