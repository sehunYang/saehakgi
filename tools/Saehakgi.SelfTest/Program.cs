using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Saehakgi.Core.Crypto;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Migration;
using Saehakgi.Core.Migration.Modules;
using Saehakgi.Core.Native;

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

Console.WriteLine("\n[5] 레지스트리 설정 적용/초기화 (임시 키로 실제 설정과 격리)");

Check("kind별 적용 → 초기화가 이전값 복원/신규값 삭제", () =>
{
    var subKey = @"Software\saehakgi-selftest-" + Guid.NewGuid().ToString("N");
    try
    {
        // Pre-existing value that must be restored on reset.
        using (var seed = Registry.CurrentUser.CreateSubKey(subKey, true))
            seed.SetValue("StrVal", "OLD", RegistryValueKind.String);

        var entries = new List<RegEntry>
        {
            new(subKey, "StrVal", "hello", "String"),
            new(subKey, "ExpandVal", @"%USERPROFILE%\x", "ExpandString"),
            new(subKey, "DwordVal", "7", "DWord"),
        };

        var applied = new AppliedManifest();
        RegistryValues.Apply(entries, applied);

        using (var k = Registry.CurrentUser.OpenSubKey(subKey)!)
        {
            if ((string?)k.GetValue("StrVal") != "hello") throw new Exception("StrVal not applied");
            if (k.GetValueKind("ExpandVal") != RegistryValueKind.ExpandString) throw new Exception("ExpandVal kind wrong");
            if ((int)k.GetValue("DwordVal")! != 7) throw new Exception("DwordVal not applied");
        }

        new MigrationEngine().Reset(applied);

        using (var k = Registry.CurrentUser.OpenSubKey(subKey)!)
        {
            if ((string?)k.GetValue("StrVal") != "OLD") throw new Exception("reset did not restore prior StrVal");
            if (k.GetValue("ExpandVal") is not null) throw new Exception("reset did not delete new ExpandVal");
            if (k.GetValue("DwordVal") is not null) throw new Exception("reset did not delete new DwordVal");
        }
    }
    finally
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
    }
});

Console.WriteLine("\n[6] 설치/시작 프로그램 수집 (읽기 전용)");

Check("설치 프로그램 레지스트리 목록 수집", () =>
{
    var programs = InstalledProgramsModule.EnumerateInstalled();
    if (programs.Count == 0) throw new Exception("expected at least one installed program");
    Console.WriteLine($"         설치 프로그램: {programs.Count}개 (예: {programs[0].Name})");
});

Check("시작프로그램 수집", () =>
{
    var staging = Path.Combine(Path.GetTempPath(), "saehakgi-startup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    try
    {
        var items = new StartupProgramsModule().Collect(new MigrationRequest(), staging);
        Console.WriteLine($"         시작프로그램 항목: {items.Count}개" + (items.Count > 0 ? $" ({items[0].DisplayName})" : " (없음)"));
    }
    finally { try { Directory.Delete(staging, recursive: true); } catch { } }
});

Console.WriteLine("\n[7] 명령 기반 되돌리기 + Wi-Fi/전원/폰트");

Check("허용 목록 외(cmd) 초기화 명령은 실행되지 않음", () =>
{
    var sentinel = Path.Combine(Path.GetTempPath(), "saehakgi-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
    var applied = new AppliedManifest();
    applied.Actions.Add(new AppliedAction
    {
        Kind = AppliedActionKind.RunProcessOnReset,
        Target = "blocked",
        ResetExe = "cmd",
        ResetArgs = $"/c echo x > \"{sentinel}\"",
    });

    new MigrationEngine().Reset(applied);

    if (File.Exists(sentinel)) { File.Delete(sentinel); throw new Exception("non-allowlisted command was executed"); }
});

Check("Wi-Fi / 전원 / 폰트 수집 (읽기 전용)", () =>
{
    var staging = Path.Combine(Path.GetTempPath(), "saehakgi-net-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    try
    {
        var wifi = new WifiProfilesModule().Collect(new MigrationRequest(), staging);
        var power = new PowerPlanModule().Collect(new MigrationRequest(), staging);
        var fonts = new FontsModule().Collect(new MigrationRequest(), staging);
        Console.WriteLine($"         Wi-Fi 항목 {wifi.Count} / 전원 항목 {power.Count} / 폰트 항목 {fonts.Count}");
    }
    finally { try { Directory.Delete(staging, recursive: true); } catch { } }
});

Console.WriteLine("\n[8] 설치 프로그램 선택 필터 (winget.json)");

Check("선택한 winget id만 남기고 필터링", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "saehakgi-wg-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var src = Path.Combine(dir, "full.json");
        File.WriteAllText(src, """
        { "Sources": [ { "Packages": [
            {"PackageIdentifier":"Pkg.A"},
            {"PackageIdentifier":"Pkg.B"},
            {"PackageIdentifier":"Pkg.C"}
        ], "SourceDetails": {"Name":"winget"} } ] }
        """);

        var dest = Path.Combine(dir, "filtered.json");
        InstalledProgramsModule.WriteFilteredJson(src, dest, new HashSet<string>(new[] { "Pkg.A", "Pkg.C" }, StringComparer.OrdinalIgnoreCase));

        var ids = InstalledProgramsModule.ReadPackageIds(dest);
        if (!ids.SequenceEqual(new[] { "Pkg.A", "Pkg.C" })) throw new Exception("filter kept: " + string.Join(",", ids));
    }
    finally { try { Directory.Delete(dir, recursive: true); } catch { } }
});

Console.WriteLine("\n[9] 설치 체크리스트 HTML 생성/분류");

Check("winget 미대상만 노출, 자동 성공은 숨김, 실패분은 강등", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "saehakgi-html-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var programs = Path.Combine(dir, "programs.txt");
        File.WriteAllLines(programs, new[]
        {
            "Google Chrome\t120.0\tGoogle LLC",
            "한글 2020\t\t한글과컴퓨터",
            "GPKI 행정전자서명 인증서\t\t행정안전부",
            "Microsoft Visual Studio Code\t1.90\tMicrosoft",
        });
        var winget = Path.Combine(dir, "winget.json");
        File.WriteAllText(winget, """
        { "Sources":[ { "Packages":[
            {"PackageIdentifier":"Google.Chrome"},
            {"PackageIdentifier":"Microsoft.VisualStudioCode"}
        ], "SourceDetails":{"Name":"winget"} } ] }
        """);
        var html = Path.Combine(dir, "check.html");

        // Before install: only non-winget apps listed; winget-covered hidden.
        InstalledProgramsModule.WriteChecklistHtml(programs, winget, null, html, null);
        var t1 = File.ReadAllText(html);
        if (!t1.Contains("한글 2020") || !t1.Contains("GPKI 행정전자서명")) throw new Exception("수동 항목 누락");
        if (t1.Contains("Google Chrome") || t1.Contains("Microsoft Visual Studio Code")) throw new Exception("자동 대상이 노출됨");

        // After install: a failed winget id is demoted onto the manual list.
        InstalledProgramsModule.WriteChecklistHtml(programs, winget, null, html, new[] { "Microsoft.VisualStudioCode" });
        var t2 = File.ReadAllText(html);
        if (!t2.Contains("Microsoft Visual Studio Code")) throw new Exception("실패한 winget 항목이 강등되지 않음");
        if (t2.Contains("Google Chrome")) throw new Exception("성공한 winget 항목이 노출됨");
        Console.WriteLine("         OK (미대상: 한글·GPKI / 실패강등: VSCode / 성공숨김: Chrome)");
    }
    finally { try { Directory.Delete(dir, recursive: true); } catch { } }
});

Console.WriteLine("\n[10] 브라우저 쿠키 네이티브 메시징");

Check("메시지 프레이밍 왕복", () =>
{
    var ms = new MemoryStream();
    NativeMessaging.WriteMessage(ms, "{\"cmd\":\"ping\"}");
    ms.Position = 0;
    if (NativeMessaging.ReadMessage(ms) != "{\"cmd\":\"ping\"}") throw new Exception("framing mismatch");
    if (NativeMessaging.ReadMessage(ms) != null) throw new Exception("expected EOF");
});

Check("ping 응답", () =>
{
    var resp = NativeHost.Handle("{\"cmd\":\"ping\"}");
    if (!resp.Contains("\"ok\":true") || !resp.Contains(NativeHost.Version)) throw new Exception(resp);
});

Check("쿠키 put→get 왕복 + 잘못된 암호 거부", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "saehakgi-ck-" + Guid.NewGuid().ToString("N") + ".dat");
    try
    {
        var put = new JsonObject
        {
            ["cmd"] = "put_cookies", ["path"] = path, ["passphrase"] = "pw",
            ["cookies"] = new JsonArray(new JsonObject
            {
                ["name"] = "sid", ["value"] = "abc123", ["domain"] = "example.com", ["path"] = "/", ["secure"] = true,
            }),
        }.ToJsonString();
        var putResp = NativeHost.Handle(put);
        if (!putResp.Contains("\"ok\":true") || !putResp.Contains("\"count\":1")) throw new Exception("put: " + putResp);

        var get = new JsonObject { ["cmd"] = "get_cookies", ["path"] = path, ["passphrase"] = "pw" }.ToJsonString();
        var getResp = NativeHost.Handle(get);
        if (!getResp.Contains("\"ok\":true") || !getResp.Contains("abc123") || !getResp.Contains("sid")) throw new Exception("get: " + getResp);

        var bad = new JsonObject { ["cmd"] = "get_cookies", ["path"] = path, ["passphrase"] = "WRONG" }.ToJsonString();
        if (!NativeHost.Handle(bad).Contains("\"ok\":false")) throw new Exception("wrong passphrase accepted");
    }
    finally { try { File.Delete(path); } catch { } }
});

Check("RunLoop 다중 메시지 처리", () =>
{
    var input = new MemoryStream();
    NativeMessaging.WriteMessage(input, "{\"cmd\":\"ping\"}");
    NativeMessaging.WriteMessage(input, "{\"cmd\":\"nope\"}");
    input.Position = 0;
    var output = new MemoryStream();
    NativeHost.RunLoop(input, output);
    output.Position = 0;
    var r1 = NativeMessaging.ReadMessage(output);
    var r2 = NativeMessaging.ReadMessage(output);
    if (r1 is null || !r1.Contains("\"ok\":true")) throw new Exception("r1: " + r1);
    if (r2 is null || !r2.Contains("\"ok\":false")) throw new Exception("r2: " + r2);
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
