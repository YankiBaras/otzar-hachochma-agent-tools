# IT-742: build dist\otzar-mcp.exe as a Node.js single executable application (SEA).
# Build machine only: needs Node 22+ (the target PC needs nothing). Nothing is downloaded.
# The two steps that the "postject" tool normally does are done here with the Windows API:
#   1. add the SEA blob to a copy of node.exe as the RCDATA resource NODE_SEA_BLOB;
#   2. flip the SEA fuse string in the binary from ":0" to ":1".
# The copied node.exe is Authenticode-signed; the signature would be broken by step 1, so the
# certificate table is removed first. The result is an unsigned exe.
param(
    [string]$NodeExe = (Get-Command node -ErrorAction Stop).Source  # a win-x86 node.exe gives an x86 build
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $here 'dist'
$work = Join-Path $here 'build-tmp'
New-Item -ItemType Directory -Force $dist, $work | Out-Null

$main = Join-Path $here 'src\otzar-mcp.cjs'
$blob = Join-Path $work 'sea-prep.blob'
$cfg  = Join-Path $work 'sea-config.json'
$json = @{ main = $main; output = $blob; disableExperimentalSEAWarning = $true; useCodeCache = $false; useSnapshot = $false } | ConvertTo-Json
[IO.File]::WriteAllText($cfg, $json, (New-Object Text.UTF8Encoding $false))

& $NodeExe --experimental-sea-config $cfg
if ($LASTEXITCODE) { throw "SEA blob generation failed" }

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class SeaInject {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateResource(IntPtr h, IntPtr type, string name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr h, bool discard);

    // Removes the Authenticode certificate table (data directory 4) if it sits at the end of the file.
    public static void StripSignature(string path) {
        byte[] b = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(b, 0x3C);
        if (BitConverter.ToUInt32(b, pe) != 0x00004550) throw new Exception("not a PE file");
        int opt = pe + 24;
        ushort magic = BitConverter.ToUInt16(b, opt);
        int dirs = opt + (magic == 0x20B ? 112 : 96);
        int sec = dirs + 4 * 8;
        uint off = BitConverter.ToUInt32(b, sec), size = BitConverter.ToUInt32(b, sec + 4);
        if (off == 0 || size == 0) return;
        if (off + size != b.Length) throw new Exception("certificate table is not at the end of the file");
        Array.Clear(b, sec, 8);
        Array.Resize(ref b, (int)off);
        File.WriteAllBytes(path, b);
    }

    public static void AddBlob(string path, byte[] blob) {
        IntPtr h = BeginUpdateResource(path, false);
        if (h == IntPtr.Zero) throw new Exception("BeginUpdateResource failed: " + Marshal.GetLastWin32Error());
        if (!UpdateResource(h, (IntPtr)10, "NODE_SEA_BLOB", 0, blob, (uint)blob.Length)) {
            int e = Marshal.GetLastWin32Error(); EndUpdateResource(h, true);
            throw new Exception("UpdateResource failed: " + e);
        }
        if (!EndUpdateResource(h, false)) throw new Exception("EndUpdateResource failed: " + Marshal.GetLastWin32Error());
    }

    // The fuse must occur exactly once, as postject requires.
    public static void FlipFuse(string path, string fuse) {
        byte[] b = File.ReadAllBytes(path);
        byte[] f = Encoding.ASCII.GetBytes(fuse + ":0");
        int found = -1, count = 0;
        for (int i = 0; i <= b.Length - f.Length; i++) {
            int j = 0; while (j < f.Length && b[i + j] == f[j]) j++;
            if (j == f.Length) { found = i; count++; }
        }
        if (count != 1) throw new Exception("SEA fuse found " + count + " times (expected 1)");
        b[found + f.Length - 1] = (byte)'1';
        File.WriteAllBytes(path, b);
    }
}
'@

$exe = Join-Path $dist 'otzar-mcp.exe'
Copy-Item $NodeExe $exe -Force
[SeaInject]::StripSignature($exe)
[SeaInject]::AddBlob($exe, [IO.File]::ReadAllBytes($blob))
[SeaInject]::FlipFuse($exe, 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2')

Remove-Item -Recurse -Force $work
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB) SHA256 $hash"
