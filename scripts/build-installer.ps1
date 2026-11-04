# Deprecated: this script referenced the removed artifacts/installer/transflux.iss layout and an
# old FFmpeg folder, so running it produced a wrong or failing build. The supported entry point is
# tools/package-exe.ps1 (with packaging/TransFlux.iss). It is kept only as a guard so stale
# instructions fail loudly instead of silently producing an old installer.
throw 'scripts/build-installer.ps1 is deprecated. Use: pwsh -File tools/package-exe.ps1 [-Version x.y.z.w] [-Architecture x64|arm64]'
