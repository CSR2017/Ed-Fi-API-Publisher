# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
    .SYNOPSIS
        Automation script for running build operations from the command line.
    .DESCRIPTION
        Provides automation of the following tasks:
        
        * Build: runs `dotnet build` with several implicit steps
          (clean, restore, inject version information).
        * Publish: creates the publish files.
        * Package: create nuget package.
        * Push: Push package to azure artifacts.        
        * UnitTest: runs the unit tests.  

    .EXAMPLE
        .\build.ps1 build -Configuration Release -Version "2.0.0"
        Overrides the default build configuration (Debug) to build in release
        mode with assembly version 2.0.0.45.
	
    .EXAMPLE
        .\build.ps1 Publish
        Output: Publish files will be created.
    
    .EXAMPLE
        .\build.ps1 Package -Version "2.0.0"
        Output: Create nuget package with EdFi.ApiPublisher, EdFi.ApiPublisher.Win64 folders
        
	.EXAMPLE
        .\build.ps1 UnitTest
        Output: Test results will be logged to .trx files.
    
    .EXAMPLE
        .\build.ps1 Push -NuGetApiKey $env:nuget_key
        Output: Application nuget package will be pushed to azure artifacts.

#>
[CmdLetBinding()]

param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Build", "BuildAndPublish", "Package", "Push", "UnitTest")]
    $Command = "Build",

  #  [switch] $SelfContained,

    # Assembly and package version number, defaults 2.6.1
    [string]
    $Version = "0.1.0",

    # .NET project build configuration, defaults to "Debug". Options are: Debug, Release.
    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Debug",

    # Ed-Fi's official NuGet package feed for package download and distribution.
    [string]
    $EdFiNuGetFeed = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    # API key for accessing the feed above. Only required with with the Push
    # command.
    [string]
    $NuGetApiKey,

    # Full path of a package file to push to the NuGet feed. Optional, only
    # applies with the Push command. If not set, then the script looks for a
    # NuGet package corresponding to the provided $Version.
    [string]
    $PackageFile
)

$Env:MSBUILDDISABLENODEREUSE = "1"

$solutionRoot = "$PSScriptRoot"
$maintainers = "Ed-Fi Alliance, LLC and contributors"
Import-Module -Name ("$PSScriptRoot/eng/build-helpers.psm1") -Force
$cliProject = "EdFi.Tools.ApiPublisher.Cli"
$publishOutputPath = "$solutionRoot/$cliProject/publish"
$publishFddOutputDirectory = "$publishOutputPath/fdd"
$publishScdOutputDirectory = "$publishOutputPath/scd"
$testProjectName = "EdFi.Tools.ApiPublisher.Tests"

function Clean {
    Invoke-Execute { dotnet clean $solutionRoot -c $Configuration --nologo -v minimal }
}

function AssemblyInfo {
    Invoke-Execute {
        $assembly_version = $Version
        
		$year = (Get-Date -Format yyyy)

        Invoke-RegenerateFile ("$solutionRoot/Directory.Build.props") @"
<Project>
    <!-- This file is generated by the build script. -->
    <PropertyGroup>
        <Product>Ed-Fi API-Publisher</Product>
        <Authors>$maintainers</Authors>
        <Company>$maintainers</Company>
        <Copyright>Copyright © $year Ed-Fi Alliance</Copyright>
        <VersionPrefix>$assembly_version</VersionPrefix>
        <VersionSuffix></VersionSuffix>
    </PropertyGroup>
</Project>
 
"@
    }
}

function Compile {
    Invoke-Execute {
        dotnet --info
        dotnet build $solutionRoot -c $Configuration --nologo
    }
}

function Publish {
    Invoke-Execute {
        $project = "$solutionRoot/$cliProject"
        Write-Host "Self contained." -ForegroundColor Cyan
        dotnet publish $project -c $Configuration /p:EnvironmentName=Production -o "$publishScdOutputDirectory" --self-contained -r win10-x64 --nologo
    
        Write-Host "Not self contained." -ForegroundColor Cyan
        dotnet publish $project -c $Configuration /p:EnvironmentName=Production -o "$publishFddOutputDirectory" --no-self-contained --no-build --nologo
    }
}

function RunDotNetPack {
    param (
        [string]
        $PackageVersion,

        [string]
        $ProjectName,

        [string]
        $NuspecFileName
    )

    dotnet pack "$ProjectName.csproj" --no-build --no-restore --output "$PSScriptRoot" --configuration $Configuration -p:NuspecFile="$NuspecFileName.nuspec" -p:NuspecProperties="version=$PackageVersion"
}


function Package {
    Invoke-Execute {
        $baseProjectFullName = "$solutionRoot/$cliProject/$cliProject"  
        RunDotNetPack -PackageVersion $Version -projectName $baseProjectFullName $baseProjectFullName            
    }
}

function Push {
    param (
        [string]
        $PackageVersion = $Version       
    )

    if (-not $NuGetApiKey) {
        throw "Cannot push a NuGet package without providing an API key in the `NuGetApiKey` argument."
    }

    if (-not $PackageFile) {         
         $PackageFile = "$PSScriptRoot/EdFi.ApiPublisher.$PackageVersion.nupkg"  
         DotnetPush  $PackageFile       
    }
    else
    {
        DotnetPush  $PackageFile
    }
}

function DotnetPush {
    param (
        [string]
        $PackageFileName
    )

    Write-Host "Pushing $PackageFileName to $EdFiNuGetFeed"
    dotnet nuget push $PackageFileName --api-key $NuGetApiKey --source $EdFiNuGetFeed
}

function RunTests {
    param (
        # File search filter
        [string]
        $Filter,
		[string]
        $Category
    )

    $testAssemblyPath = "$solutionRoot/$Filter/bin/$Configuration"
    $testAssemblies = Get-ChildItem -Path $testAssemblyPath -Filter "$Filter.dll" -Recurse

    if ($testAssemblies.Length -eq 0) {
        Write-Host "no test assemblies found in $testAssemblyPath"
    }

    $testAssemblies | ForEach-Object {
        Write-Host "Executing: dotnet test $($_)"
        Invoke-Execute { 
            dotnet test --filter Category=$Category $_ `
            --logger "trx;LogFileName=$($_).trx" `
            --nologo `
            --no-build
        }
    }
}

function UnitTests {
    Invoke-Execute { RunTests -Filter $testProjectName -Category UnitTest}
}

function Invoke-Build {
    Write-Host "Building Version $Version" -ForegroundColor Cyan
    Invoke-Step { Clean }
    Invoke-Step { AssemblyInfo }
    Invoke-Step { Compile }
}

function Invoke-BuildAndPublish {
    Invoke-Build
    Invoke-Step { Publish }
}

function Invoke-UnitTests {
    Invoke-Step { UnitTests }
}

function Invoke-Package {
    Invoke-Step { Package }
}

function Invoke-Push {
    Invoke-Step { Push }
}

Invoke-Main {
    switch ($Command) {       
        Build { Invoke-Build }
        UnitTest { Invoke-UnitTests }		
        BuildAndPublish { Invoke-BuildAndPublish }
        Package { Invoke-Package }
        Push { Invoke-Push }
        default { throw "Command '$Command' is not recognized" }
    }
}