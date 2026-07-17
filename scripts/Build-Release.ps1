param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$DownloadPrevious
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish'))
$releaseDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'Releases'))
$projectPath = Join-Path $repoRoot 'GeminiDesk\GeminiDesk.csproj'
$appIconPath = Join-Path $repoRoot 'GeminiDesk\Assets\bunny-app.ico'

if (-not $publishDir.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $releaseDir.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '배포 출력 경로가 artifacts 폴더 밖을 가리킵니다.'
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Push-Location $repoRoot

try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 도구 복원에 실패했습니다.' }

    if ($DownloadPrevious) {
        dotnet vpk download github `
            --repoUrl 'https://github.com/wlaxhrl/GeminiDesk' `
            --outputDir $releaseDir `
            --channel win

        if ($LASTEXITCODE -ne 0) {
            Write-Warning '이전 릴리스를 받지 못해 전체 패키지만 만듭니다.'
        }
    }

    dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDir `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'GeminiDesk publish에 실패했습니다.' }

    dotnet vpk pack `
        --packId GeminiDesk `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe GeminiDesk.exe `
        --packTitle 'Bunny Desk' `
        --packAuthors wlaxhrl `
        --icon $appIconPath `
        --runtime win-x64 `
        --outputDir $releaseDir
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 패키지 생성에 실패했습니다.' }

    Write-Host "배포 파일 생성 완료: $releaseDir"
}
finally {
    Pop-Location
}
