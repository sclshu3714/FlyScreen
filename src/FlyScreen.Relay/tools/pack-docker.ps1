#!/usr/bin/env pwsh
<#
.SYNOPSIS
  打包 FlyScreen.Relay Docker 镜像（构建 / 导出 / 推送）。

.EXAMPLE
  .\tools\pack-docker.ps1 -Mirror
  .\tools\pack-docker.ps1 -Mirror -Tag 1.0.0 -Save
  .\tools\pack-docker.ps1 -Tag 1.0.0 -Registry registry.example.com/flyscreen -Push
  .\tools\pack-docker.ps1 -LoadPath .\out\flyscreen-relay-1.0.0.tar
#>
[CmdletBinding(DefaultParameterSetName = "Build")]
param(
  [Parameter(ParameterSetName = "Build")]
  [string]$ImageName = "flyscreen-relay",

  [Parameter(ParameterSetName = "Build")]
  [string]$Tag = "",

  [Parameter(ParameterSetName = "Build")]
  [string]$Registry = "",

  [Parameter(ParameterSetName = "Build")]
  [switch]$Save,

  [Parameter(ParameterSetName = "Build")]
  [string]$OutDir = "",

  [Parameter(ParameterSetName = "Build")]
  [switch]$Push,

  [Parameter(ParameterSetName = "Build")]
  [switch]$NoCache,

  # 使用国内镜像加速基础镜像拉取（DaoCloud mcr 代理）
  [Parameter(ParameterSetName = "Build")]
  [switch]$Mirror,

  [Parameter(ParameterSetName = "Build")]
  [string]$SdkImage = "",

  [Parameter(ParameterSetName = "Build")]
  [string]$RuntimeImage = "",

  [Parameter(ParameterSetName = "Load")]
  [string]$LoadPath = ""
)

$ErrorActionPreference = "Stop"
$RelayRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RelayRoot

function Assert-Docker {
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "未找到 docker 命令，请先安装并启动 Docker Desktop。"
  }
  docker info 1>$null 2>$null
  if ($LASTEXITCODE -ne 0) {
    throw "Docker 引擎未就绪，请启动 Docker Desktop 后重试。"
  }
}

function Resolve-Tag([string]$explicit) {
  if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit.Trim() }

  $git = Get-Command git -ErrorAction SilentlyContinue
  if ($git) {
    $describe = (& git -C $RelayRoot describe --tags --always --dirty 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($describe)) {
      return ($describe.Trim() -replace '[\\/]', '-')
    }
  }

  return (Get-Date -Format "yyyyMMdd.HHmmss")
}

function Get-ImageRefs([string]$name, [string]$tag, [string]$registry) {
  $localLatest = "${name}:latest"
  $localTagged = "${name}:${tag}"
  $refs = @($localLatest, $localTagged)
  if (-not [string]::IsNullOrWhiteSpace($registry)) {
    $prefix = $registry.TrimEnd('/')
    $refs += @("${prefix}/${name}:latest", "${prefix}/${name}:${tag}")
  }
  return ,$refs
}

Assert-Docker

if ($PSCmdlet.ParameterSetName -eq "Load") {
  if ([string]::IsNullOrWhiteSpace($LoadPath) -or -not (Test-Path -LiteralPath $LoadPath)) {
    throw "LoadPath 无效: $LoadPath"
  }
  Write-Host "==> docker load -i $LoadPath"
  docker load -i $LoadPath
  if ($LASTEXITCODE -ne 0) { throw "docker load 失败 (exit $LASTEXITCODE)" }
  Write-Host "完成：已从 $LoadPath 加载镜像"
  exit 0
}

$resolvedTag = Resolve-Tag $Tag
$imageRefs = Get-ImageRefs $ImageName $resolvedTag $Registry
$primary = $imageRefs[0]

Write-Host "==> 工程目录: $RelayRoot"
Write-Host "==> 镜像标签: $($imageRefs -join ', ')"

if (-not (Test-Path -LiteralPath (Join-Path $RelayRoot "Dockerfile"))) {
  throw "未找到 Dockerfile: $RelayRoot"
}

if ($Mirror) {
  if ([string]::IsNullOrWhiteSpace($SdkImage)) { $SdkImage = "mcr.m.daocloud.io/dotnet/sdk:10.0" }
  if ([string]::IsNullOrWhiteSpace($RuntimeImage)) { $RuntimeImage = "mcr.m.daocloud.io/dotnet/aspnet:10.0" }
}

$buildArgs = @("build")
if ($NoCache) { $buildArgs += "--no-cache" }
if (-not [string]::IsNullOrWhiteSpace($SdkImage)) {
  $buildArgs += @("--build-arg", "SDK_IMAGE=$SdkImage")
  Write-Host "==> SDK_IMAGE=$SdkImage"
}
if (-not [string]::IsNullOrWhiteSpace($RuntimeImage)) {
  $buildArgs += @("--build-arg", "RUNTIME_IMAGE=$RuntimeImage")
  Write-Host "==> RUNTIME_IMAGE=$RuntimeImage"
}
foreach ($ref in $imageRefs) {
  $buildArgs += @("-t", $ref)
}
$buildArgs += "."

Write-Host "==> docker $($buildArgs -join ' ')"
& docker @buildArgs
if ($LASTEXITCODE -ne 0) { throw "docker build 失败 (exit $LASTEXITCODE)" }

$exportPath = $null
if ($Save) {
  if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $RelayRoot "out"
  }
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $exportPath = Join-Path $OutDir ("{0}-{1}.tar" -f $ImageName, $resolvedTag)
  Write-Host "==> docker save -o $exportPath $($imageRefs -join ' ')"
  & docker save -o $exportPath @imageRefs
  if ($LASTEXITCODE -ne 0) { throw "docker save 失败 (exit $LASTEXITCODE)" }

  $sizeMb = [math]::Round((Get-Item -LiteralPath $exportPath).Length / 1MB, 2)
  Write-Host "已导出: $exportPath ($sizeMb MB)"
}

if ($Push) {
  if ([string]::IsNullOrWhiteSpace($Registry)) {
    throw "使用 -Push 时必须指定 -Registry，例如 -Registry registry.example.com/flyscreen"
  }
  $remoteRefs = $imageRefs | Where-Object { $_ -like "$($Registry.TrimEnd('/'))/*" }
  foreach ($ref in $remoteRefs) {
    Write-Host "==> docker push $ref"
    docker push $ref
    if ($LASTEXITCODE -ne 0) { throw "docker push 失败: $ref (exit $LASTEXITCODE)" }
  }
}

Write-Host ""
Write-Host "打包完成"
Write-Host "  主镜像: $primary"
Write-Host "  版本标签: ${ImageName}:${resolvedTag}"
if ($exportPath) { Write-Host "  导出文件: $exportPath" }
Write-Host ""
Write-Host "本地运行示例:"
Write-Host "  docker run -d --name flyscreen-relay ``"
Write-Host "    -p 8443:8443 -p 9080:9080 -p 15155:15155 -p 8443:8443/udp -p 15156:15156/udp ``"
Write-Host "    -e Relay__AdminToken=change-me ``"
Write-Host "    $primary"
Write-Host "  或: docker compose up -d"
