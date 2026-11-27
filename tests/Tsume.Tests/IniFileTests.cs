using System.Text;
using Tsume.Upscaling.Config;

namespace Tsume.Tests;

public sealed class IniFileTests
{
    [Fact]
    public void Parse_ReadsSectionsKeysCaseInsensitivelyAndIgnoresComments()
    {
        var ini = IniFile.Parse("""
            ; Leading comment
            # Hash comment
            [Upscalers]
            ; CommentedOut=true
            Dx12Upscaler = xess
            # AnotherComment=1
            Dx11Upscaler=fsr31
            """);

        Assert.Equal("xess", ini.Get("Upscalers", "Dx12Upscaler"));
        Assert.Equal("xess", ini.Get("upscalers", "dx12upscaler"));
        Assert.Equal("fsr31", ini.Get("UPSCALERS", "DX11UPSCALER"));
        Assert.Null(ini.Get("Upscalers", "CommentedOut"));
        Assert.Null(ini.Get("Upscalers", "AnotherComment"));
        Assert.Null(ini.Get("Upscalers", "MissingKey"));
        Assert.Null(ini.Get("MissingSection", "Dx12Upscaler"));
    }

    [Fact]
    public void Set_UpdatesExistingKeyInPlacePreservingKeyCasingCommentsAndLineEndings()
    {
        const string original = "; Header comment\r\n[Upscalers]\r\n; Choose backend\r\nDx12Upscaler=auto\r\nDx11Upscaler=auto\r\n";
        var ini = IniFile.Parse(original);

        ini.Set("upscalers", "dx12upscaler", "ffx");

        const string expected = "; Header comment\r\n[Upscalers]\r\n; Choose backend\r\nDx12Upscaler=ffx\r\nDx11Upscaler=auto\r\n";
        Assert.Equal(expected, ini.ToString());
    }

    [Fact]
    public void Set_InsertsNewKeyAfterLastSettingBeforeNextSectionBanner()
    {
        const string original = "[First]\nA=1\n\n; Banner for second section\n[Second]\nB=2\n";
        var ini = IniFile.Parse(original);

        ini.Set("First", "C", "3");

        const string expected = "[First]\nA=1\nC=3\n\n; Banner for second section\n[Second]\nB=2\n";
        Assert.Equal(expected, ini.ToString());
    }

    [Fact]
    public void Set_InsertsNewKeyIntoEmptySection()
    {
        const string original = "[Empty]\n[Next]\nKey=Value\n";
        var ini = IniFile.Parse(original);

        ini.Set("Empty", "Added", "yes");

        const string expected = "[Empty]\nAdded=yes\n[Next]\nKey=Value\n";
        Assert.Equal(expected, ini.ToString());
    }

    [Fact]
    public void Set_InsertsNewKeyIntoTrailingEmptySectionPreservingTrailingNewline()
    {
        var iniLf = IniFile.Parse("[Empty]\n");
        iniLf.Set("Empty", "Added", "yes");
        Assert.Equal("[Empty]\nAdded=yes\n", iniLf.ToString());

        var iniCrlf = IniFile.Parse("[Empty]\r\n");
        iniCrlf.Set("Empty", "Added", "yes");
        Assert.Equal("[Empty]\r\nAdded=yes\r\n", iniCrlf.ToString());
    }

    [Fact]
    public void Set_OnEmptyFileCreatesSectionWithoutLeadingBlankLine()
    {
        var ini = IniFile.Parse("");
        ini.Set("Upscalers", "Dx12Upscaler", "xess");
        Assert.Equal("[Upscalers]\nDx12Upscaler=xess", ini.ToString());
    }

    [Fact]
    public void Set_CreatesMissingSectionWithLeadingBlankLine()
    {
        const string original = "[Existing]\nKey=Value";
        var ini = IniFile.Parse(original);

        ini.Set("Added", "Flag", "true");

        const string expected = "[Existing]\nKey=Value\n\n[Added]\nFlag=true";
        Assert.Equal(expected, ini.ToString());
    }

    [Fact]
    public void LoadAndSave_RoundTripOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsume-ini-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "OptiScaler.ini");

            var empty = IniFile.Load(path);
            Assert.Null(empty.Get("Upscalers", "Dx12Upscaler"));

            empty.Set("Upscalers", "Dx12Upscaler", "xess");
            empty.Set("FSR", "UpscalerIndex", "0");
            empty.Save(path);

            var reloaded = IniFile.Load(path);
            Assert.Equal("xess", reloaded.Get("Upscalers", "Dx12Upscaler"));
            Assert.Equal("0", reloaded.Get("FSR", "UpscalerIndex"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAndSave_PreservesUtf8BomWhenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsume-ini-bom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "OptiScaler.ini");
            const string content = "[Upscalers]\r\nDx12Upscaler=auto\r\n";
            var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(path, content, utf8Bom);

            var ini = IniFile.Load(path);
            Assert.Equal("auto", ini.Get("Upscalers", "Dx12Upscaler"));

            ini.Set("Upscalers", "Dx12Upscaler", "xess");
            ini.Save(path);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);

            var savedText = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Equal("[Upscalers]\r\nDx12Upscaler=xess\r\n", savedText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAndSave_DoesNotAddUtf8BomWhenAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tsume-ini-nobom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "OptiScaler.ini");
            const string content = "[Upscalers]\nDx12Upscaler=auto\n";
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(path, content, utf8NoBom);

            var ini = IniFile.Load(path);
            ini.Set("Upscalers", "Dx12Upscaler", "fsr31");
            ini.Save(path);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("[Upscalers]\nDx12Upscaler=fsr31\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseAndSet_PreservesMixedLineEndingsWithoutStrayCarriageReturns()
    {
        const string original = "; Header\r\n[Upscalers]\nDx12Upscaler=auto\r\nDx11Upscaler=auto\n[FSR]\r\nUpscalerIndex=0\n";
        var ini = IniFile.Parse(original);

        Assert.Equal("auto", ini.Get("Upscalers", "Dx12Upscaler"));
        Assert.Equal("auto", ini.Get("Upscalers", "Dx11Upscaler"));
        Assert.Equal("0", ini.Get("FSR", "UpscalerIndex"));

        // Unmodified round-trip preserves exact mixed line endings byte-for-byte.
        Assert.Equal(original, ini.ToString());

        // Editing both a CRLF line and an LF line keeps each line's original terminator and introduces no stray \r.
        ini.Set("Upscalers", "Dx12Upscaler", "xess");
        ini.Set("Upscalers", "Dx11Upscaler", "fsr31");

        const string expected = "; Header\r\n[Upscalers]\nDx12Upscaler=xess\r\nDx11Upscaler=fsr31\n[FSR]\r\nUpscalerIndex=0\n";
        Assert.Equal(expected, ini.ToString());
        Assert.DoesNotContain("\r\r", ini.ToString(), StringComparison.Ordinal);
    }
}
