#!/usr/bin/env pwsh
<#
.SYNOPSIS
  打包 FlyScreen.Relay Docker 镜像（构建 / 导出 / 推送）。

.EXAMPLE
  .\tools\pack-docker.ps1 -Mirror
  .\tools\pack-docker.ps1 -Mirror -Tag 1.0.0 -Save
  .\tools\pack-docker.ps1 -Tag 1.0.0 -Registry registry.example.com/flyscreen -Push
  .\tools\pack-docker.ps1 -SshHost 192.168.1.10 -SshUser build -SshKey $HOME\.ssh\id_rsa -Mirror -Tag 1.1.0 -Save
  .\tools\pack-docker.ps1 -LoadPath .\out\flyscreen-relay-1.0.0.tar

  远程密码请用环境变量 FLYSCREEN_DOCKER_SSH_PASSWORD（勿写进命令行）。
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

  # 构建后不清理悬空 <none> 镜像（默认会 prune，避免多阶段/重复打包堆积）
  [Parameter(ParameterSetName = "Build")]
  [switch]$NoPrune,

  # 远程 Docker（SSH）。空 / localhost / 127.0.0.1 = 本机
  [Parameter(ParameterSetName = "Build")]
  [string]$SshHost = "",

  [Parameter(ParameterSetName = "Build")]
  [int]$SshPort = 22,

  [Parameter(ParameterSetName = "Build")]
  [string]$SshUser = "",

  [Parameter(ParameterSetName = "Build")]
  [string]$SshKey = "",

  # tcp = 局域网无认证（DOCKER_HOST=tcp://host:port）；ssh = 需用户+密钥/密码
  [Parameter(ParameterSetName = "Build")]
  [ValidateSet("auto", "tcp", "ssh")]
  [string]$Transport = "auto",

  # 也可直接传完整 DOCKER_HOST（优先于 Ssh*）
  [Parameter(ParameterSetName = "Build")]
  [string]$DockerHost = "",

  [Parameter(ParameterSetName = "Load")]
  [string]$LoadPath = ""
)

$ErrorActionPreference = "Stop"
$RelayRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RelayRoot
$script:_sshTmpDir = $null

function Add-DockerToPath {
  # 优先 Docker Desktop（自带 cli-plugins/buildx），再回退其它 docker.exe 所在目录
  $candidates = @(
    (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin'),
    (Join-Path $env:ProgramFiles 'Docker\Docker\resources'),
    (Join-Path ${env:ProgramFiles(x86)} 'Docker\Docker\resources\bin'),
    (Join-Path $env:ProgramFiles 'docker'),
    (Join-Path ${env:ProgramFiles(x86)} 'docker')
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

  foreach ($dir in $candidates) {
    $dockerExe = Join-Path $dir 'docker.exe'
    if (-not (Test-Path -LiteralPath $dockerExe)) { continue }
    $parts = @($env:Path -split ';' | Where-Object { $_ })
    if ($parts -notcontains $dir) {
      $env:Path = "$dir;$env:Path"
      Write-Host "==> 已将 Docker 加入 PATH: $dir"
    }
    return
  }
}

function ConvertTo-DockerLogLine($obj) {
  if ($null -eq $obj) { return $null }
  if ($obj -is [System.Management.Automation.ErrorRecord]) {
    $msg = $obj.Exception.Message
    if ([string]::IsNullOrWhiteSpace($msg)) { $msg = $obj.ToString() }
    # buildx/进度常向 stderr 写空帧，ToString 会变成类型名噪音
    if ([string]::IsNullOrWhiteSpace($msg)) { return $null }
    if ($msg -eq 'System.Management.Automation.RemoteException') { return $null }
    return $msg
  }
  $s = "$obj"
  if ([string]::IsNullOrWhiteSpace($s)) { return $null }
  if ($s -eq 'System.Management.Automation.RemoteException') { return $null }
  return $s
}

function Invoke-DockerRaw {
  param(
    [Parameter(Mandatory)][string[]]$DockerArgs,
    [switch]$PassThru
  )
  # stderr（如 DEPRECATED）在 $ErrorActionPreference=Stop 下会被当成终止错误，必须降级。
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $lines = New-Object System.Collections.Generic.List[string]
  try {
    & docker @DockerArgs 2>&1 | ForEach-Object {
      $s = ConvertTo-DockerLogLine $_
      if ($null -eq $s) { return }
      [void]$lines.Add($s)
      # Write-Host：不污染函数返回值；空 RemoteException 已在 ConvertTo-DockerLogLine 过滤
      Write-Host $s
    }
    $code = $LASTEXITCODE
  }
  finally {
    $ErrorActionPreference = $prev
  }

  if ($PassThru) {
    return @{ Code = $code; Text = ($lines -join "`n") }
  }
  return $code
}

function Test-IsLocalDockerHost([string]$hostName) {
  if ([string]::IsNullOrWhiteSpace($hostName)) { return $true }
  $h = $hostName.Trim().ToLowerInvariant()
  return ($h -eq 'localhost' -or $h -eq '127.0.0.1' -or $h -eq '::1' -or $h -eq '(local)')
}

function Initialize-RemoteDockerHost {
  if (-not [string]::IsNullOrWhiteSpace($DockerHost)) {
    $env:DOCKER_HOST = $DockerHost.Trim()
    Write-Host "==> DOCKER_HOST=$($env:DOCKER_HOST)"
    return
  }

  if (Test-IsLocalDockerHost $SshHost) {
    if ($env:DOCKER_HOST) {
      Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
      Write-Host "==> 本机 Docker（已清除 DOCKER_HOST）"
    }
    else {
      Write-Host "==> 本机 Docker Desktop"
    }
    return
  }

  $hostName = $SshHost.Trim()
  $port = if ($SshPort -gt 0) { $SshPort } else { 2375 }
  $user = $SshUser.Trim()
  $useTcp = ($Transport -eq "tcp") -or ($Transport -eq "auto" -and [string]::IsNullOrWhiteSpace($user))
  if ($Transport -eq "ssh") { $useTcp = $false }

  if ($useTcp) {
    if ($port -lt 1 -or $port -gt 65535) { $port = 2375 }
    $env:DOCKER_HOST = "tcp://${hostName}:${port}"
    Write-Host "==> 远程 Docker（TCP 无认证）: $($env:DOCKER_HOST)"
    Write-Host "    提示：远端 Docker Desktop / Engine 须允许未加密 TCP（仅建议局域网）。"
    return
  }

  if ([string]::IsNullOrWhiteSpace($user)) {
    throw "SSH 模式须指定 -SshUser；局域网无账号请用 -Transport tcp。"
  }
  if ($port -lt 1 -or $port -gt 65535) { $port = 22 }

  $key = $SshKey.Trim()
  $password = $env:FLYSCREEN_DOCKER_SSH_PASSWORD
  if ([string]::IsNullOrWhiteSpace($key) -and [string]::IsNullOrWhiteSpace($password)) {
    throw "SSH 模式须提供 -SshKey，或设置环境变量 FLYSCREEN_DOCKER_SSH_PASSWORD。"
  }
  if (-not [string]::IsNullOrWhiteSpace($key) -and -not (Test-Path -LiteralPath $key)) {
    throw "SSH 密钥不存在: $key"
  }

  # 命令行脚本仍可走本机 OpenSSH；FlyScreen.Setup 已改用 SSH.NET 隧道并传 -DockerHost。
  if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw "未找到 ssh 命令。请安装 OpenSSH 客户端，或由 Setup 使用 SSH.NET（-DockerHost），或改用 -Transport tcp / 本机 Docker。"
  }

  $tmpDir = Join-Path ([IO.Path]::GetTempPath()) ("flyscreen-docker-ssh-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
  $configPath = Join-Path $tmpDir 'ssh_config'
  $askPassPath = Join-Path $tmpDir 'askpass.cmd'

  $configLines = @(
    "Host flyscreen-docker-build",
    "  HostName $hostName",
    "  Port $port",
    "  User $user",
    "  StrictHostKeyChecking accept-new",
    "  UserKnownHostsFile $tmpDir\known_hosts"
  )
  if (-not [string]::IsNullOrWhiteSpace($key)) {
    $configLines += @(
      "  IdentityFile `"$key`"",
      "  IdentitiesOnly yes"
    )
  }
  Set-Content -LiteralPath $configPath -Value ($configLines -join "`n") -Encoding ascii

  $env:DOCKER_HOST = "ssh://flyscreen-docker-build"
  # Docker CLI 调用 ssh 时读取此配置
  $env:DOCKER_SSH = "ssh -F `"$configPath`""

  if (-not [string]::IsNullOrWhiteSpace($password) -and [string]::IsNullOrWhiteSpace($key)) {
    # OpenSSH askpass：无密钥时走密码
    $escaped = $password.Replace('"', '""')
    Set-Content -LiteralPath $askPassPath -Value "@echo off`r`necho $escaped" -Encoding ascii
    $env:SSH_ASKPASS = $askPassPath
    $env:SSH_ASKPASS_REQUIRE = "force"
    $env:DISPLAY = "localhost:0"
    Write-Host "==> 远程 Docker（SSH 密码认证）: ${user}@${hostName}:${port}"
  }
  else {
    Write-Host "==> 远程 Docker（SSH 密钥）: ${user}@${hostName}:${port}"
  }

  Write-Host "==> DOCKER_HOST=$($env:DOCKER_HOST)"
  $script:_sshTmpDir = $tmpDir
}

function Clear-RemoteDockerSession {
  $dir = $script:_sshTmpDir
  if ($dir -and (Test-Path -LiteralPath $dir)) {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  }
  $script:_sshTmpDir = $null
  Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
  Remove-Item Env:DOCKER_SSH -ErrorAction SilentlyContinue
  Remove-Item Env:SSH_ASKPASS -ErrorAction SilentlyContinue
  Remove-Item Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
  Remove-Item Env:FLYSCREEN_DOCKER_SSH_PASSWORD -ErrorAction SilentlyContinue
}

function Assert-Docker {
  Add-DockerToPath
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "未找到 docker 命令。请安装 Docker Desktop，或确认 PATH 含 Docker\resources\bin。"
  }
  $info = Invoke-DockerRaw -DockerArgs @('info') -PassThru
  if ($info.Code -ne 0) {
    $msg = ("$(if ($null -ne $info.Text) { $info.Text } else { '' })").Trim()
    if ($env:DOCKER_HOST) {
      throw "远程 Docker 不可用（DOCKER_HOST=$($env:DOCKER_HOST)）。请检查 IP/端口/账号与 SSH。`n$msg"
    }
    throw "Docker 引擎未就绪，请启动 Docker Desktop 后重试。`n$msg"
  }
}

function Test-DockerBuildxAvailable {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $null = & docker buildx version 2>$null
    return ($LASTEXITCODE -eq 0)
  }
  catch {
    return $false
  }
  finally {
    $ErrorActionPreference = $prev
  }
}

function Find-DockerBuildxExe {
  $candidates = @(
    (Join-Path $env:USERPROFILE '.docker\cli-plugins\docker-buildx.exe'),
    (Join-Path $env:ProgramFiles 'Docker\Docker\resources\cli-plugins\docker-buildx.exe'),
    (Join-Path $env:ProgramFiles 'Docker\cli-plugins\docker-buildx.exe'),
    (Join-Path $env:ProgramFiles 'docker\cli-plugins\docker-buildx.exe')
  )
  foreach ($p in $candidates) {
    if ($p -and (Test-Path -LiteralPath $p)) { return $p }
  }
  return $null
}

function Ensure-DockerBuildx {
  if (Test-DockerBuildxAvailable) { return $true }

  $pluginDir = Join-Path $env:USERPROFILE '.docker\cli-plugins'
  $target = Join-Path $pluginDir 'docker-buildx.exe'
  New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

  $found = Find-DockerBuildxExe
  if ($found -and -not [string]::Equals($found, $target, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $found -Destination $target -Force
    Write-Host "==> 已安装 buildx 插件: $target"
    if (Test-DockerBuildxAvailable) { return $true }
  }

  # Docker 23+ / 29 客户端构建依赖 buildx；精简 docker.exe 常缺插件，尝试拉取官方二进制
  $ver = 'v0.23.0'
  $url = "https://github.com/docker/buildx/releases/download/$ver/buildx-$ver.windows-amd64.exe"
  $tmp = Join-Path ([IO.Path]::GetTempPath()) ("docker-buildx-" + [guid]::NewGuid().ToString('N') + ".exe")
  try {
    Write-Host "==> 未检测到 buildx，正在下载 $ver …"
    Write-Host "    $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    Move-Item -LiteralPath $tmp -Destination $target -Force
    Write-Host "==> 已安装 buildx 插件: $target"
  }
  catch {
    Write-Warning "自动安装 buildx 失败: $($_.Exception.Message)"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    return $false
  }

  return (Test-DockerBuildxAvailable)
}

function Initialize-DockerBuildKit {
  if (Ensure-DockerBuildx) {
    $env:DOCKER_BUILDKIT = "1"
    Write-Host "==> DOCKER_BUILDKIT=1（buildx 可用）"
    return $true
  }

  # Docker 29 起经典 builder 往往仅警告后仍失败；仅作最后回退
  $env:DOCKER_BUILDKIT = "0"
  Write-Warning "未检测到 docker buildx。将尝试经典 builder（Docker 29+ 可能已移除，建议安装 Docker Desktop 或 buildx 插件）。"
  Write-Host "==> DOCKER_BUILDKIT=0（回退经典 builder）"
  return $false
}

function Invoke-DockerBuild([string[]]$buildArgs) {
  Write-Host "==> docker $($buildArgs -join ' ')"
  $result = Invoke-DockerRaw -DockerArgs $buildArgs -PassThru
  if ($result.Code -eq 0) { return }

  $text = if ($null -ne $result.Text) { [string]$result.Text } else { "" }
  $needBuildx = $text -match 'buildx component is missing|BuildKit is enabled but the buildx'
  $needClassic = $needBuildx -or ($text -match 'legacy builder is deprecated')

  if ($needBuildx) {
    Write-Warning "构建需要 buildx，尝试修复插件后重试…"
    if (Ensure-DockerBuildx) {
      $env:DOCKER_BUILDKIT = "1"
      Write-Host "==> DOCKER_BUILDKIT=1（buildx 已就绪，重试）"
      Write-Host "==> docker $($buildArgs -join ' ')"
      $retry = Invoke-DockerRaw -DockerArgs $buildArgs -PassThru
      if ($retry.Code -eq 0) { return }
      throw "docker build 失败 (exit $($retry.Code))（已安装 buildx 仍失败）"
    }
  }

  if ($needClassic -and $env:DOCKER_BUILDKIT -ne "0") {
    Write-Warning "BuildKit 不可用，回退经典 builder 重试…"
    $env:DOCKER_BUILDKIT = "0"
    Write-Host "==> DOCKER_BUILDKIT=0（回退重试）"
    Write-Host "==> docker $($buildArgs -join ' ')"
    $retry = Invoke-DockerRaw -DockerArgs $buildArgs -PassThru
    if ($retry.Code -eq 0) { return }
    throw "docker build 失败 (exit $($retry.Code))。请安装 Docker Desktop，或将 buildx 放到 %USERPROFILE%\.docker\cli-plugins\docker-buildx.exe"
  }

  throw "docker build 失败 (exit $($result.Code))"
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
  # 本地仍打 latest + 版本，方便 compose；导出 tar 只带版本标签，避免 load 后出现两条镜像
  $localLatest = "${name}:latest"
  $localTagged = "${name}:${tag}"
  $refs = @($localLatest, $localTagged)
  if (-not [string]::IsNullOrWhiteSpace($registry)) {
    $prefix = $registry.TrimEnd('/')
    $refs += @("${prefix}/${name}:latest", "${prefix}/${name}:${tag}")
  }
  return ,$refs
}

function Get-SaveRefs([string[]]$refs, [string]$name, [string]$tag) {
  $versionRef = "${name}:${tag}"
  $list = @($versionRef)
  if (-not [string]::IsNullOrWhiteSpace($Registry)) {
    $prefix = $Registry.TrimEnd('/')
    $remote = "${prefix}/${name}:${tag}"
    if ($refs -contains $remote) { $list += $remote }
  }
  return ,$list
}

try {
  Initialize-RemoteDockerHost
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

  # BuildKit：有 buildx 时启用（减少 <none>）；远程 TCP 等无 buildx 则用经典 builder
  Initialize-DockerBuildKit | Out-Null

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

  Invoke-DockerBuild $buildArgs

  if (-not $NoPrune) {
    Write-Host "==> docker image prune -f  （清理悬空 <none> 镜像）"
    docker image prune -f
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "docker image prune 失败 (exit $LASTEXITCODE)，可稍后手动执行: docker image prune -f"
    }
  }

  $exportPath = $null
  if ($Save) {
    if ([string]::IsNullOrWhiteSpace($OutDir)) {
      $OutDir = Join-Path $RelayRoot "out"
    }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $exportPath = Join-Path $OutDir ("{0}-{1}.tar" -f $ImageName, $resolvedTag)
    $saveRefs = Get-SaveRefs $imageRefs $ImageName $resolvedTag
    Write-Host "==> docker save -o $exportPath $($saveRefs -join ' ')  （仅版本标签，避免 load 出两条）"
    & docker save -o $exportPath @saveRefs
    if ($LASTEXITCODE -ne 0) { throw "docker save 失败 (exit $LASTEXITCODE)" }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $exportPath).Length / 1MB, 2)
    Write-Host "已导出: $exportPath ($sizeMb MB)"
    Write-Host "  load 后若需 latest: docker tag ${ImageName}:${resolvedTag} ${ImageName}:latest"
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
  Write-Host "    -v flyscreen-relay-data:/app/data ``"
  Write-Host "    ${ImageName}:${resolvedTag}"
  Write-Host "  或: docker tag ${ImageName}:${resolvedTag} ${ImageName}:latest && docker compose up -d"
  Write-Host "  群晖等面板：镜像不会自动带出 STUN 的 UDP 8443，请手动加 容器端口 8443 / UDP（勿填「8443/udp」字面量）"
}
finally {
  Clear-RemoteDockerSession
}
