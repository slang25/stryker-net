# AGENTS.md - Stryker.NET Development Guide

This document provides guidance for AI agents and developers working on the Stryker.NET codebase.

## Project Overview

**Stryker.NET** is a mutation testing framework for .NET applications. It temporarily inserts bugs (mutations) into source code and runs tests to validate test suite effectiveness. If tests catch the mutation (fail), the mutant is "killed"; if tests pass, the mutant "survived" indicating potential gaps in test coverage.

- **Repository**: https://github.com/stryker-mutator/stryker-net
- **Published as**: `dotnet-stryker` NuGet tool
- **Target Framework**: .NET 8.0
- **Minimum Supported**: .NET Core 1.1, .NET Framework 4.5, .NET Standard 1.3

## Quick Reference

### Build

```bash
dotnet build src/Stryker.sln
```

### Run All Unit Tests

```bash
dotnet test src/Stryker.sln
```

### Run Tests for a Specific Project

```bash
# Core mutation logic tests (largest test suite)
dotnet test src/Stryker.Core/Stryker.Core.UnitTest/Stryker.Core.UnitTest.csproj

# CLI tests
dotnet test src/Stryker.CLI/Stryker.CLI.UnitTest/Stryker.CLI.UnitTest.csproj

# Regex mutator tests
dotnet test src/Stryker.RegexMutators/Stryker.RegexMutators.UnitTest/Stryker.RegexMutators.UnitTest.csproj

# Test runner tests
dotnet test src/Stryker.TestRunner.VsTest.UnitTest/Stryker.TestRunner.VsTest.UnitTest.csproj

# Solution parsing tests
dotnet test src/Stryker.Solutions.Test/Stryker.Solutions.Test.csproj
```

### Run Integration Tests

Requires PowerShell 7+:

```bash
./integration-tests.ps1
```

Or specify category and runtime:

```bash
CATEGORY=SingleTestProject RUNTIME=netcore ./integration-tests.ps1
```

Integration test categories: `InitCommand`, `SingleTestProject`, `MultipleTestProjects`, `Solution`

## Project Structure

```
src/
├── Stryker.sln                    # Main solution file
├── Directory.Build.props          # Centralized build properties (net8.0 target)
├── Directory.Build.targets        # InternalsVisibleTo for testing
├── Directory.Packages.props       # Central NuGet package versions
│
├── Stryker.Abstractions/          # Core interfaces (IMutant, IMutator, etc.)
├── Stryker.CLI/                   # Command-line interface
│   ├── Stryker.CLI/               # CLI implementation
│   └── Stryker.CLI.UnitTest/      # CLI tests
├── Stryker.Core/                  # Core mutation testing logic
│   ├── Stryker.Core/              # Mutators, compiling, filtering
│   └── Stryker.Core.UnitTest/     # Core tests (1400+ tests)
├── Stryker.DataCollector/         # Test data collection
├── Stryker.Options/               # Configuration models
├── Stryker.RegexMutators/         # Regex-specific mutations
├── Stryker.Solutions/             # Solution file parsing
├── Stryker.TestRunner/            # Test execution abstraction
├── Stryker.TestRunner.VsTest/     # VsTest runner implementation
└── Stryker.Utilities/             # Common utilities
```

## Key Concepts

### Mutators

Mutators are the core of Stryker. They transform syntax nodes to create mutations. Located in `src/Stryker.Core/Stryker.Core/Mutators/`:

- **BinaryExpressionMutator**: Arithmetic/comparison operators (`+` → `-`, `>` → `<`)
- **BooleanMutator**: `true` ↔ `false`
- **NegateConditionMutator**: Logical negation (`!`)
- **LinqMutator**: LINQ methods (`Any` → `All`)
- **MathMutator**: `Math.Abs`, `Math.Max` mutations
- **StringMutator**: String literal mutations

All mutators inherit from `MutatorBase<T>` and implement `IMutator`.

### Key Abstractions (in Stryker.Abstractions)

- `IMutant` - Represents a code mutation with status
- `IMutator` - Interface for mutation strategies
- `MutantStatus` - Enum: Killed, Survived, Timeout, NoCoverage, etc.
- `Mutation` - Data model for code changes

### Mutation Workflow

1. **Initialization**: Analyze project structure via Buildalyzer
2. **Mutation**: Transform syntax trees using Roslyn
3. **Compilation**: Build mutated code, rollback on errors
4. **Testing**: Run tests against each mutant
5. **Reporting**: Generate HTML/JSON reports

## Test Framework

- **Test Framework**: MSTest v3
- **Mocking**: Moq 4.20
- **Assertions**: Shouldly 4.3
- **File System**: TestableIO.System.IO.Abstractions

### Test Patterns

Tests use file system abstractions for isolation:

```csharp
[TestMethod]
public void MyTest()
{
    var fileSystem = new MockFileSystem();
    // ... test with mocked file system
}
```

Mutator tests verify syntax transformations:

```csharp
[TestMethod]
public void ShouldMutateBinaryExpression()
{
    var source = "var x = 1 + 2;";
    var mutator = new BinaryExpressionMutator();
    // ... verify mutations
}
```

## Code Style

The project uses `.editorconfig` for style enforcement:

- **Indentation**: 4 spaces (2 for scripts, JSON, YAML)
- **Naming**: PascalCase for public, `_camelCase` for private fields
- **Braces**: Always use braces (even for single-line blocks)
- **var**: Prefer `var` when type is apparent
- **Namespaces**: File-scoped namespaces

Follow [Microsoft C# coding conventions](https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/inside-a-program/coding-conventions).

## Adding a New Mutator

See `adding_a_mutator.md` for detailed instructions. Summary:

1. Create a class inheriting `MutatorBase<T>` in `src/Stryker.Core/Stryker.Core/Mutators/`
2. Add entry to `Mutator` enum in `Stryker.Abstractions`
3. Register in `CsharpMutantOrchestrator` constructor
4. Add unit tests in `src/Stryker.Core/Stryker.Core.UnitTest/Mutators/`
5. Update `docs/mutations.md`

### Mutator Guidelines

- Generate mutations that look like real human errors
- Avoid mutations that always cause exceptions
- Ensure mutations are killable by tests
- Support various C# syntax versions

## Development Tools

- **.NET Compiler Platform SDK**: Install via Visual Studio for Syntax Visualizer
- **Roslyn Quoter**: http://roslynquoter.azurewebsites.net/ - Generate SyntaxFactory code
- **SharpLab**: https://sharplab.io/ - Visualize AST and IL

## Debugging

Set `Stryker.CLI` as startup project in Visual Studio with working directory pointing to a test project:

```
WorkingDirectory: ./integrationtest/TargetProjects/NetCore/NetCoreTestProject.XUnit
```

For verbose output:

```bash
dotnet stryker --verbosity debug
```

## CI/CD

- **Azure Pipelines**: `azure-pipelines.yml` - Main CI, SonarCloud analysis, releases
- **GitHub Actions**: `.github/workflows/` - Integration tests, stryker-on-stryker

Tests run on: Windows, macOS, Ubuntu × .NET Core / .NET Framework

## Commit Conventions

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <subject>

Types: feat, fix, docs, style, refactor, test, chore
```

Example: `feat(mutators): add string comparison mutations`

## Important Files

| File | Purpose |
|------|---------|
| `src/Stryker.sln` | Main solution |
| `src/Directory.Packages.props` | NuGet package versions |
| `integration-tests.ps1` | Integration test runner |
| `docs/configuration.md` | Configuration reference |
| `docs/mutations.md` | Mutation types documentation |

## Known Platform Differences

Some tests are skipped on non-Windows platforms:
- .NET Framework-specific tests (MSBuild paths)
- Windows-specific file path handling

## Package Management

Central package versioning via `Directory.Packages.props`. Lock files (`packages.lock.json`) ensure reproducible builds with `RestoreLockedMode=true`.

## Getting Help

- GitHub Issues: https://github.com/stryker-mutator/stryker-net/issues
- Slack: Stryker community workspace
- Documentation: `/docs` directory
