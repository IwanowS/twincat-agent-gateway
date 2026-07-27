# TwinCAT XAE Base interop assemblies

These assemblies are generated from the Beckhoff TwinCAT XAE Base type
libraries installed with TwinCAT 3.1. They let the repository build with
`dotnet build` without invoking MSBuild's Windows-only `ResolveComReference`
task.

The XAE project uses version `3.1` by default. Select another checked-in
version at build time with:

```powershell
dotnet build src/TwinCatGateway.Xae/TwinCatGateway.Xae.csproj -p:TwinCatXaeBaseVersion=3.3
```

All variants use the `TCatSysManagerLib` namespace and the
`Interop.TCatSysManagerLib` assembly name. The assembly version follows the
source type-library version.

## Provenance

Type library GUID: `{3C49D6C3-93DC-11D0-B162-00A0248C244B}`

| Version | Source TLB SHA-256 | Generated DLL SHA-256 |
| --- | --- | --- |
| 2.1 | `3ea6b8be91be96811ff84d907ff2d73de2ed7a899e14f4d3f5b07a9230066aa1` | `30809249a1c2704314a4066de02e61dbab473f5a3b9bf0b292522ef8b2fd2d2b` |
| 3.1 | `66fa4d5cbe9148020882b152c20a226783d3c0aaabf7bfac843be57a3bdd51ef` | `eeff7f8dcd250770acab80c9728ac061d397bc016d67e824f01f13c89ad43033` |
| 3.2 | `b9a5661e8f3d41e2612f5ed879a6140098f9881be62f29079b0b97393a93cb0d` | `4a750fd141a7cdbabe88784721528b50d1a796ed648a77e16feaa7c2ce79cf6d` |
| 3.3 | `ab26e24ce8afdb597092b46220c09ef7bdf782017b5bbdcf950ff20af70156bc` | `b27772387c509178feecf9b99b28bcdfb8ca65f7aa44cbce9842650d30163f63` |

Source layout:

```text
C:\TwinCAT\3.1\Components\Base\TypeLib\<version>\TCatSysManager.tlb
```

Generated with the .NET Framework 4.8 SDK `TlbImp.exe`:

```powershell
TlbImp.exe TCatSysManager.tlb `
  /out:Interop.TCatSysManagerLib.dll `
  /namespace:TCatSysManagerLib `
  /machine:X86 `
  /nologo
```

The checked-in DLL is the build input. Regeneration requires a local Beckhoff
installation that supplies the matching type library and must update both
hashes in this file.
