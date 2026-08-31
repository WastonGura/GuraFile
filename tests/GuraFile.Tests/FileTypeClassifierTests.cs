using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileTypeClassifierTests
{
    [TestMethod]
    [DataRow("photo.PNG", "类型/图片", "格式/PNG")]
    [DataRow("movie.mp4", "类型/视频", "格式/MP4")]
    [DataRow("song.mp3", "类型/音频", "格式/MP3")]
    [DataRow("notes.md", "类型/文档", "格式/Markdown")]
    [DataRow("table.xlsx", "类型/表格", "格式/XLSX")]
    [DataRow("slides.pptx", "类型/演示文稿", "格式/PPTX")]
    [DataRow("archive.7z", "类型/压缩包", "格式/7Z")]
    [DataRow("source.cs", "类型/代码", "格式/CS")]
    [DataRow("program.exe", "类型/可执行文件", "格式/EXE")]
    [DataRow("font.ttf", "类型/字体", "格式/TTF")]
    [DataRow("unknown.xyz", "类型/其他", "格式/XYZ")]
    public void ExtensionProducesCoarseAndSpecificTags(string fileName, string typeTag, string formatTag)
    {
        var classifier = new FileTypeClassifier((_, _) => []);

        var result = classifier.Classify(fileName);

        Assert.AreEqual(typeTag, result.TypeTag);
        Assert.AreEqual(formatTag, result.FormatTag);
        Assert.IsFalse(result.HasConflict);
        CollectionAssert.AreEquivalent(new[] { typeTag, formatTag }, result.AutomaticTags.ToArray());
    }

    [TestMethod]
    [DataRow("photo.png", "89504E470D0A1A0A", "格式/PNG")]
    [DataRow("photo.jpg", "FFD8FFE000104A464946", "格式/JPEG")]
    [DataRow("image.gif", "474946383961", "格式/GIF")]
    [DataRow("document.pdf", "255044462D312E37", "格式/PDF")]
    [DataRow("archive.zip", "504B030414000000", "格式/ZIP")]
    [DataRow("archive.7z", "377ABCAF271C0004", "格式/7Z")]
    [DataRow("audio.mp3", "49443304000000000000", "格式/MP3")]
    [DataRow("video.mp4", "000000186674797069736F6D000000000000000000000000", "格式/MP4")]
    public void StableHeadersAreRecognized(string fileName, string headerHex, string detectedFormatTag)
    {
        var header = Convert.FromHexString(headerHex);
        var classifier = new FileTypeClassifier((_, _) => header);

        var result = classifier.Classify(fileName);

        Assert.AreEqual(detectedFormatTag, result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    public void ConflictingHeaderIsReportedWithoutChangingExtensionClassification()
    {
        var pngHeader = Convert.FromHexString("89504E470D0A1A0A");
        var classifier = new FileTypeClassifier((_, _) => pngHeader);

        var result = classifier.Classify("notes.txt");

        Assert.AreEqual("类型/文档", result.TypeTag);
        Assert.AreEqual("格式/TXT", result.FormatTag);
        Assert.AreEqual("格式/PNG", result.DetectedFormatTag);
        Assert.IsTrue(result.HasConflict);
        CollectionAssert.Contains(result.AutomaticTags.ToArray(), "状态/类型冲突");
    }

    [TestMethod]
    public void ZipContainerIsCompatibleWithOfficeFormats()
    {
        var zipHeader = Convert.FromHexString("504B030414000000");
        var classifier = new FileTypeClassifier((_, _) => zipHeader);

        var result = classifier.Classify("report.docx");

        Assert.AreEqual("格式/DOCX", result.FormatTag);
        Assert.AreEqual("格式/ZIP", result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    [DataRow("FFFE0000")]
    [DataRow("FFFFFFFF")]
    [DataRow("FFFD9064")]
    [DataRow("FFFF9064")]
    public void InvalidMpegHeadersAreNotClassifiedAsMp3(string headerHex)
    {
        var classifier = new FileTypeClassifier((_, _) => Convert.FromHexString(headerHex));

        var result = classifier.Classify("notes.txt");

        Assert.IsNull(result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    public void ValidMpegFrameHeaderIsClassifiedAsMp3()
    {
        var classifier = new FileTypeClassifier((_, _) => Convert.FromHexString("FFFB9064"));

        var result = classifier.Classify("audio.mp3");

        Assert.AreEqual("格式/MP3", result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    [DataRow("video.mp4", "000000186674797069736F6D000000000000000000000000", "格式/MP4")]
    [DataRow("audio.m4a", "00000018667479704D344120000000000000000000000000", "格式/M4A")]
    [DataRow("movie.mov", "000000186674797071742020000000000000000000000000", "格式/MOV")]
    [DataRow("photo.heic", "000000186674797068656963000000000000000000000000", "格式/HEIC")]
    [DataRow("photo.avif", "000000186674797061766966000000000000000000000000", "格式/AVIF")]
    [DataRow("clip.3gp", "000000186674797033677034000000000000000000000000", "格式/3GP")]
    public void IsoBaseMediaBrandsAreDistinguished(string fileName, string headerHex, string detectedFormatTag)
    {
        var classifier = new FileTypeClassifier((_, _) => Convert.FromHexString(headerHex));

        var result = classifier.Classify(fileName);

        Assert.AreEqual(detectedFormatTag, result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    [DataRow("000000086674797069736F6D")]
    [DataRow("000000187878787869736F6D")]
    [DataRow("0000001866747970756E6B6E")]
    [DataRow("000000186674797069736F6D")]
    public void InvalidOrUnknownIsoBaseMediaHeaderIsIgnored(string headerHex)
    {
        var classifier = new FileTypeClassifier((_, _) => Convert.FromHexString(headerHex));

        var result = classifier.Classify("file.bin");

        Assert.IsNull(result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    public void HeaderClassifiesFilesWithoutAnExtension()
    {
        var classifier = new FileTypeClassifier((_, _) => Convert.FromHexString("255044462D312E37"));

        var result = classifier.Classify("README");

        Assert.AreEqual("类型/文档", result.TypeTag);
        Assert.AreEqual("格式/PDF", result.FormatTag);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    public void HeaderReadIsBoundedAndFailuresAreNonBlocking()
    {
        var requestedBytes = 0;
        var bounded = new FileTypeClassifier((_, maxBytes) =>
        {
            requestedBytes = maxBytes;
            return new byte[maxBytes];
        });
        var failing = new FileTypeClassifier((_, _) => throw new UnauthorizedAccessException("denied"));

        var boundedResult = bounded.Classify("large.bin");
        var failedResult = failing.Classify("locked.pdf");

        Assert.AreEqual(FileTypeClassifier.HeaderByteLimit, requestedBytes);
        Assert.IsNull(boundedResult.Diagnostic);
        Assert.AreEqual("类型/文档", failedResult.TypeTag);
        Assert.AreEqual("格式/PDF", failedResult.FormatTag);
        StringAssert.Contains(failedResult.Diagnostic, "denied");
    }

    [TestMethod]
    public void ShortUnknownHeaderDoesNotThrow()
    {
        var classifier = new FileTypeClassifier((_, _) => [0x01]);

        var result = classifier.Classify("file");

        Assert.AreEqual("类型/其他", result.TypeTag);
        Assert.AreEqual("格式/未知", result.FormatTag);
        Assert.IsNull(result.DetectedFormatTag);
        Assert.IsFalse(result.HasConflict);
    }
}
