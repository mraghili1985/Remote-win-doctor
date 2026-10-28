# Remote-win-doctor

RemoteWinDoctor: remote Windows diagnostics and optimization over LAN (WinRM).

- `RemoteWinDoctor.Desktop/`: WPF desktop app (.NET 10)
- `RemoteWinDoctor/`: shared services and the web dashboard (ASP.NET Core, port 5055)

Build the portable single-file exe from `RemoteWinDoctor.Desktop`:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ../Portable
```
