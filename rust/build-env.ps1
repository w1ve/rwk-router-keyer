# C/link toolchain for building rwk-core on this machine, entirely from E:.
#
# Why this exists: the only Visual Studio install here is a stripped Insiders
# build with no MSVC CRT headers (no vcruntime.h) and no standard lib\x64, so
# ring (pulled in by boringtun) cannot compile with it. Instead we use:
#   * clang + lld-link from E:\LLVM (LLVM 23.1.2)
#   * the MSVC CRT + Windows SDK headers/libs fetched to E:\winsdk via xwin
# Nothing here depends on C: beyond the OS itself.

$llvm   = "E:\LLVM"
$winsdk = "E:\winsdk"

# Compiler + linker (LLVM). ring's build script invokes $env:CC to compile C;
# Rust links the final binary with lld-link via -C linker.
$env:PATH = "$llvm\bin;$env:PATH"
$env:CC   = "clang-cl"
$env:CXX  = "clang-cl"
$env:AR   = "llvm-lib"

# Headers clang-cl searches (CRT first, then the SDK families).
$env:INCLUDE = @(
    "$winsdk\crt\include",
    "$winsdk\sdk\include\ucrt",
    "$winsdk\sdk\include\um",
    "$winsdk\sdk\include\shared"
) -join ';'

# Import libraries the linker searches.
$env:LIB = @(
    "$winsdk\crt\lib\x86_64",
    "$winsdk\sdk\lib\ucrt\x86_64",
    "$winsdk\sdk\lib\um\x86_64"
) -join ';'

# Tell Rust/Cargo to link with LLVM's lld-link rather than MSVC link.exe.
$env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = "$llvm\bin\lld-link.exe"

# Keep rustdoc/cargo's scratch space on a real, present temp dir. (A stale TMP pointing at
# a since-deleted folder makes `cargo test` fail its doctest pass with a NotFound.)
$env:TMP  = Join-Path $env:LOCALAPPDATA 'Temp'
$env:TEMP = $env:TMP

Write-Host "build-env ready:"
Write-Host "  clang-cl : $((Get-Command clang-cl).Source)"
Write-Host "  lld-link : $llvm\bin\lld-link.exe"
Write-Host "  INCLUDE  : $env:INCLUDE"
Write-Host "  LIB      : $env:LIB"
