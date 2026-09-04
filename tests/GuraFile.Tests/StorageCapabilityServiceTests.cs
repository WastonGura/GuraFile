using System.IO;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class StorageCapabilityServiceTests
{
    [TestMethod]
    public void Probe_UncPath_IdentifiesAsNetworkAndPathDegraded()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => null,
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"\\server\share\subfolder");

        Assert.AreEqual(StorageMediumKind.Network, capability.MediumKind);
        Assert.AreEqual("SMB", capability.FileSystemName);
        Assert.IsFalse(capability.SupportsStableFileId);
        Assert.IsFalse(capability.IsReparsePoint);
        StringAssert.Contains(capability.UserSummary, "网络共享 (SMB)");
        StringAssert.Contains(capability.UserSummary, "身份跟踪受限（路径降级）");
    }

    [TestMethod]
    public void Probe_MappedNetworkDrive_IdentifiesAsNetworkAndPathDegraded()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("Z:\\", DriveType.Network, "SMB", IsReady: true),
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"Z:\SharedDocs");

        Assert.AreEqual(StorageMediumKind.Network, capability.MediumKind);
        Assert.AreEqual("SMB", capability.FileSystemName);
        Assert.IsFalse(capability.SupportsStableFileId);
        StringAssert.Contains(capability.UserSummary, "网络共享 (SMB)");
        StringAssert.Contains(capability.UserSummary, "身份跟踪受限（路径降级）");
    }

    [TestMethod]
    public void Probe_LocalFixedDrive_WithNtfs_IdentifiesAsFixedAndStable()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("C:\\", DriveType.Fixed, "NTFS", IsReady: true),
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"C:\Projects\GuraFile");

        Assert.AreEqual(StorageMediumKind.Fixed, capability.MediumKind);
        Assert.AreEqual("NTFS", capability.FileSystemName);
        Assert.IsTrue(capability.SupportsStableFileId);
        Assert.IsFalse(capability.IsReparsePoint);
        Assert.AreEqual("本地固定盘 (NTFS) - 支持稳定身份跟踪", capability.UserSummary);
    }

    [TestMethod]
    public void Probe_LocalFixedDrive_WithRefs_IdentifiesAsFixedAndStable()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("D:\\", DriveType.Fixed, "ReFS", IsReady: true),
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"D:\Data");

        Assert.AreEqual(StorageMediumKind.Fixed, capability.MediumKind);
        Assert.AreEqual("ReFS", capability.FileSystemName);
        Assert.IsTrue(capability.SupportsStableFileId);
        Assert.AreEqual("本地固定盘 (ReFS) - 支持稳定身份跟踪", capability.UserSummary);
    }

    [TestMethod]
    public void Probe_LocalFixedDrive_WithFat32_IdentifiesAsFixedAndLimited()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("D:\\", DriveType.Fixed, "FAT32", IsReady: true),
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"D:\OldDrive");

        Assert.AreEqual(StorageMediumKind.Fixed, capability.MediumKind);
        Assert.AreEqual("FAT32", capability.FileSystemName);
        Assert.IsFalse(capability.SupportsStableFileId);
        Assert.AreEqual("本地固定盘 (FAT32) - 身份跟踪受限", capability.UserSummary);
    }

    [TestMethod]
    public void Probe_RemovableMedia_WithExFat_IdentifiesAsRemovableAndLimited()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("E:\\", DriveType.Removable, "exFAT", IsReady: true),
            getAttributes: _ => FileAttributes.Directory);

        var capability = service.Probe(@"E:\Photos");

        Assert.AreEqual(StorageMediumKind.Removable, capability.MediumKind);
        Assert.AreEqual("exFAT", capability.FileSystemName);
        Assert.IsFalse(capability.SupportsStableFileId);
        Assert.AreEqual("可移动介质 (exFAT) - 身份跟踪受限", capability.UserSummary);
    }

    [TestMethod]
    public void Probe_DisconnectedOrUnknownMedia_IdentifiesAsUnknownAndLimited()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => null,
            getAttributes: _ => throw new DirectoryNotFoundException("Device not found"));

        var capability = service.Probe(@"X:\Disconnected");

        Assert.AreEqual(StorageMediumKind.Unknown, capability.MediumKind);
        Assert.AreEqual("Unknown", capability.FileSystemName);
        Assert.IsFalse(capability.SupportsStableFileId);
        StringAssert.Contains(capability.UserSummary, "未知介质 - 身份跟踪受限");
    }

    [TestMethod]
    public void Probe_DriveNotReady_IdentifiesAsNotReadyAndLimited()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("F:\\", DriveType.Removable, null, IsReady: false),
            getAttributes: _ => throw new IOException("Device not ready"));

        var capability = service.Probe(@"F:\");

        Assert.AreEqual(StorageMediumKind.Removable, capability.MediumKind);
        Assert.IsFalse(capability.SupportsStableFileId);
        StringAssert.Contains(capability.UserSummary, "介质未就绪或已断开");
    }

    [TestMethod]
    public void Probe_ReparsePointPath_DetectsReparsePointFlag()
    {
        var service = new StorageCapabilityService(
            getDriveSnapshot: _ => new StorageDriveSnapshot("C:\\", DriveType.Fixed, "NTFS", IsReady: true),
            getAttributes: _ => FileAttributes.Directory | FileAttributes.ReparsePoint);

        var capability = service.Probe(@"C:\JunctionFolder");

        Assert.IsTrue(capability.IsReparsePoint);
        Assert.AreEqual(StorageMediumKind.Fixed, capability.MediumKind);
        Assert.IsTrue(capability.SupportsStableFileId);
        StringAssert.Contains(capability.UserSummary, "重解析点");
    }
}
