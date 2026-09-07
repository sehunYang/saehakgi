using System.Security.Cryptography;
using Saehakgi.Core.Crypto;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Migration;
using Saehakgi.Core.Migration.Modules;

// Non-interactive verification of the saehakgi core pipeline.
// Exits 0 if every check passes, 1 otherwise.

int failures = 0;

void Check(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"  [PASS] {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
    }
}

Console.WriteLine("== saehakgi core self-test ==");

Console.WriteLine("\n[1] 암호화 스트림 (chunked AES-256-GCM)");

Check("멀티청크 왕복 복호화가 원본과 일치", () =>
{
    var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 777); // spans several 1 MiB chunks
    var cipher = new MemoryStream();
    SecureBundleStream.Encrypt(new MemoryStream(data), cipher, "correct horse", chunkSize: 1 << 20);
    cipher.Position = 0;
    var plain = new MemoryStream();
    SecureBundleStream.Decrypt(cipher, plain, "correct horse");
    if (!plain.ToArray().AsSpan().SequenceEqual(data)) throw new Exception("round-trip mismatch");
});

Check("잘못된 패스프레이즈는 거부", () =>
{
    var cipher = new MemoryStream();
    SecureBundleStream.Encrypt(new MemoryStream(RandomNumberGenerator.GetBytes(4096)), cipher, "right");
    cipher.Position = 0;
    try { SecureBundleStream.Decrypt(cipher, new MemoryStream(), "wrong"); }
    catch (InvalidDataException) { return; }
    throw new Exception("wrong passphrase was not rejected");
});

Check("변조된 암호문은 탐지", () =>
{
    var cipher = new MemoryStream();
    SecureBundleStream.Encrypt(new MemoryStream(RandomNumberGenerator.GetBytes(65536)), cipher, "pw");
    var bytes = cipher.ToArray();
    bytes[^1] ^= 0xFF; // corrupt the last ciphertext/tag byte
    try { SecureBundleStream.Decrypt(new MemoryStream(bytes), new MemoryStream(), "pw"); }
    catch (InvalidDataException) { return; }
    catch (EndOfStreamException) { return; }
    throw new Exception("tampering was not detected");
});

Console.WriteLine("\n[2] 폴더 이전 왕복 (내보내기 → 가져오기 → 초기화)");

Check("삭제된 폴더가 정확히 복원되고 초기화로 제거됨", () =>
{
    var sandbox = Path.Combine(Path.GetTempPath(), "saehakgi-selftest-" + Guid.NewGuid().ToString("N"));
    var src = Path.Combine(sandbox, "MyFolder");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    File.WriteAllText(Path.Combine(src, "a.txt"), "hello 안녕");
    File.WriteAllText(Path.Combine(src, "sub", "b.bin"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(2048)));
    var expected = HashTree(src);

    var bundle = Path.Combine(sandbox, "out.saehakgi");
    var engine = new MigrationEngine();
    const string pw = "s3mester!";

    engine.Export(
        new[] { MigrationItemType.Folder },
        new MigrationRequest { FolderPaths = { src } },
        bundle, pw);

    // Simulate the fresh PC: the folder isn't there.
    Directory.Delete(src, recursive: true);
    if (Directory.Exists(src)) throw new Exception("precondition: folder should be gone");

    var applied = engine.Import(bundle, pw, new[] { MigrationItemType.Folder }, new MigrationRequest());
    if (!Directory.Exists(src)) throw new Exception("import did not recreate the folder");
    if (HashTree(src) != expected) throw new Exception("restored content differs from original");
    if (applied.Actions.Count == 0) throw new Exception("no reversible actions recorded");

    engine.Reset(applied);
    if (Directory.Exists(src)) throw new Exception("reset did not remove the created folder");

    try { Directory.Delete(sandbox, recursive: true); } catch { }
});

Console.WriteLine("\n[3] 읽기 전용 수집 확인 (시스템 변경 없음)");

Check("마우스 설정 수집", () =>
{
    var items = new MouseSettingsModule().Collect(new MigrationRequest(), Path.GetTempPath());
    Console.WriteLine("         마우스: " + (items.Count > 0 ? items[0].InlineJson : "(값 없음)"));
});

Check("즐겨찾기 수집(존재 시)", () =>
{
    var staging = Path.Combine(Path.GetTempPath(), "saehakgi-bm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    var items = new BookmarksModule().Collect(new MigrationRequest(), staging);
    Console.WriteLine($"         즐겨찾기 발견: {items.Count}개 ({string.Join(", ", items.Select(i => i.DisplayName))})");
    try { Directory.Delete(staging, recursive: true); } catch { }
});

Console.WriteLine("\n[4] 인증서(GPKI/NPKI) 이전 왕복 (주입 루트로 실제 스토어와 격리)");

Check("인증서 수집 → 표준 위치로 복원 → 초기화로 제거", () =>
{
    var sandbox = Path.Combine(Path.GetTempPath(), "saehakgi-cert-" + Guid.NewGuid().ToString("N"));
    var srcStore = Path.Combine(sandbox, "src", "NPKI");
    var certDir = Path.Combine(srcStore, "yessign", "USER", "cn=TESTUSER0011223344");
    Directory.CreateDirectory(certDir);
    File.WriteAllBytes(Path.Combine(certDir, "SignCert.der"), RandomNumberGenerator.GetBytes(512));
    File.WriteAllBytes(Path.Combine(certDir, "SignPri.key"), RandomNumberGenerator.GetBytes(1024));

    var restoreBase = Path.Combine(sandbox, "restore", "NPKI");
    var module = new CertificateModule(new[] { new CertStoreRoot(srcStore, "NPKI", restoreBase) });

    var staging = Path.Combine(sandbox, "staging");
    Directory.CreateDirectory(staging);
    var items = module.Collect(new MigrationRequest(), staging);
    if (items.Count != 1) throw new Exception($"expected 1 cert, got {items.Count}");

    var applied = new AppliedManifest();
    module.Apply(items[0], staging, applied, new MigrationRequest());

    var restoredKey = Path.Combine(restoreBase, "yessign", "USER", "cn=TESTUSER0011223344", "SignPri.key");
    if (!File.Exists(restoredKey)) throw new Exception("cert not restored to normalized NPKI location");

    new MigrationEngine().Reset(applied);
    if (Directory.Exists(Path.Combine(restoreBase, "yessign", "USER", "cn=TESTUSER0011223344")))
        throw new Exception("reset did not remove the restored cert");

    try { Directory.Delete(sandbox, recursive: true); } catch { }
});

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("== ALL PASS ==");
    return 0;
}
Console.WriteLine($"== {failures} CHECK(S) FAILED ==");
return 1;

// Produces a stable fingerprint of a directory tree (relative path + content hash).
static string HashTree(string root)
{
    root = Path.GetFullPath(root);
    var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p =>
        {
            var rel = Path.GetRelativePath(root, p);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
            return $"{rel}|{hash}";
        });
    return string.Join("\n", lines);
}
