# TwinCAT Agent Gateway

Local Windows gateway for reliable, compact automation of TwinCAT 3 XAE from coding agents.

The project is in its initial bootstrap phase. The authoritative documents are:

- [architecture and operation semantics](docs/ARCHITECTURE.md);
- [implementation milestones and acceptance criteria](docs/IMPLEMENTATION_PLAN.md);
- [local development setup](docs/DEVELOPMENT.md).

## Safety

Local TwinCAT activation is prohibited for this repository. Local development may compile code and run tests that do not change TwinCAT runtime or target state. Activation and other machine-state-changing integration scenarios require an explicitly configured remote test bench.

## Quick start

```powershell
dotnet restore TwinCatGateway.sln
dotnet build TwinCatGateway.sln --no-restore
dotnet test tests/TwinCatGateway.UnitTests/TwinCatGateway.UnitTests.csproj --no-build
dotnet test tests/TwinCatGateway.ContractTests/TwinCatGateway.ContractTests.csproj --no-build
```
