"""
deploy-prod.py -- Deploy leadmanager to 85.215.217.154 as leads.prospergenics.com

Usage:
    python deploy-prod.py

What it does:
  1. dotnet publish (Release) + vite build
  2. SSH to server, stop service, upload files
  3. Write appsettings.Production.json (JWT key, OpenAI key, etc.)
  4. Create/start Windows service on port 5070
  5. Create IIS site: leads.prospergenics.com
     - Static files from www/ for /
     - URL rewrite: /api/* -> http://localhost:5070/api/*
     - Reverse proxy via ARR
"""
import sys, os, subprocess, paramiko, json, time, tempfile

# All credentials live in deploy-secrets.json next to this script (gitignored);
# see deploy-secrets.example.json for the expected shape.
_SECRETS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'deploy-secrets.json')
with open(_SECRETS_PATH, encoding='utf-8') as _f:
    _SECRETS = json.load(_f)

HOST = _SECRETS['host']
USER = _SECRETS['user']
PASS = _SECRETS['password']
PORT = 22

LOCAL_BACKEND_SRC  = r'E:\projects\leadmanager\src\LeadManager.Api'
LOCAL_FRONTEND_SRC = r'E:\projects\leadmanager\client'
LOCAL_PUBLISH      = r'E:\projects\leadmanager\_publish\backend'
LOCAL_FRONTEND_OUT = r'E:\projects\leadmanager\client\dist'

REMOTE_ROOT    = r'C:\stores\leadmanager'
REMOTE_WWW     = REMOTE_ROOT + r'\www'
REMOTE_BACKEND = REMOTE_ROOT + r'\backend'
REMOTE_DATA    = REMOTE_ROOT + r'\data'

APP_PORT = 5070
SERVICE  = 'LeadManagerApi'
SITE     = 'leads'
HOSTNAME = 'leads.prospergenics.com'

# Stable production secrets (from deploy-secrets.json, kept out of git)
JWT_KEY        = _SECRETS['jwt_key']
OPENAI_KEY     = _SECRETS['openai_key']
GITHUB_TOKEN   = _SECRETS['github_token']
GITHUB_OWNER   = _SECRETS.get('github_owner', 'martiendejong')
CLICKUP_API_KEY = _SECRETS['clickup_api_key']
CLICKUP_JENGO_LIST = _SECRETS.get('clickup_jengo_list', '901215559249')

# ── Helpers ──────────────────────────────────────────────────────────────────

def connect():
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(HOST, port=PORT, username=USER, password=PASS, timeout=30)
    return ssh

def run(ssh, cmd, timeout=60):
    print(f'  > {cmd[:140]}')
    _, out, err = ssh.exec_command(cmd, timeout=timeout)
    stdout = out.read().decode(errors='replace').strip()
    stderr = err.read().decode(errors='replace').strip()
    if stdout: print(f'    {stdout[:400]}')
    if stderr: print(f'  ! {stderr[:300]}')
    return stdout

def sftp_mkdir(sftp, path):
    parts = path.replace('\\', '/').split('/')
    cur = ''
    for p in parts:
        if not p:
            continue
        cur = (cur + '/' + p) if cur else p
        try:
            sftp.stat(cur)
        except FileNotFoundError:
            sftp.mkdir(cur)

def upload_dir(sftp, local, remote):
    remote_posix = remote.replace('\\', '/')
    sftp_mkdir(sftp, remote_posix)
    for item in os.listdir(local):
        lpath = os.path.join(local, item)
        rpath = remote_posix + '/' + item
        if os.path.isdir(lpath):
            upload_dir(sftp, lpath, rpath)
        else:
            sftp.put(lpath, rpath)
            print(f'  upload {item}')

def get_appsettings():
    return json.dumps({
        'ConnectionStrings': {
            'DefaultConnection': r'Data Source=C:\stores\leadmanager\data\leadmanager.db'
        },
        'Jwt': {
            'Key': JWT_KEY,
            'Issuer': 'LeadManager',
            'Audience': 'LeadManagerClient',
            'ExpiryHours': 8
        },
        'OpenAI': {
            'ApiKey': OPENAI_KEY
        },
        'MinoxPoc': {
            'BaseUrl': 'http://localhost:3001'
        },
        'JengoWork': {
            'BaseUrl': 'https://tasks.prospergenics.com',
            'ApiKey': _SECRETS['jengowork_api_key']
        },
        'GitHub': {
            'Token': GITHUB_TOKEN,
            'Owner': GITHUB_OWNER
        },
        'ClickUp': {
            'ApiKey': CLICKUP_API_KEY,
            'JengoListId': CLICKUP_JENGO_LIST
        },
        'Kestrel': {
            'Endpoints': {
                'Http': {
                    'Url': f'http://localhost:{APP_PORT}'
                }
            }
        },
        'Logging': {
            'LogLevel': {
                'Default': 'Warning',
                'Microsoft.AspNetCore': 'Warning'
            }
        },
        'AllowedHosts': '*',
        'Sso': {
            'CallbackBaseUrl': 'https://leads.prospergenics.com'
        }
    }, indent=2)

# ── Build steps ──────────────────────────────────────────────────────────────

def build_backend():
    print('\n[BUILD] dotnet publish...')
    os.makedirs(LOCAL_PUBLISH, exist_ok=True)
    result = subprocess.run(
        ['dotnet', 'publish', LOCAL_BACKEND_SRC,
         '-c', 'Release',
         '-o', LOCAL_PUBLISH,
         '--nologo'],
        capture_output=True, text=True
    )
    print(result.stdout[-1000:] if result.stdout else '')
    if result.returncode != 0:
        print(result.stderr[-500:])
        raise RuntimeError('dotnet publish failed')
    print('  Backend published OK')

def build_frontend():
    print('\n[BUILD] npm run build...')
    result = subprocess.run(
        ['npm', 'run', 'build'],
        cwd=LOCAL_FRONTEND_SRC,
        capture_output=True, text=True, shell=True
    )
    print(result.stdout[-500:] if result.stdout else '')
    if result.returncode != 0:
        print(result.stderr[-500:])
        raise RuntimeError('Frontend build failed')
    print('  Frontend built OK')

# ── IIS PowerShell script ────────────────────────────────────────────────────

def build_iis_script():
    proxy_url = f'http://localhost:{APP_PORT}'
    return (
        'Import-Module WebAdministration -ErrorAction SilentlyContinue\n'
        f'$siteName = \'{SITE}\'\n'
        f'$poolName = \'{SITE}\'\n'
        f'$wwwPath  = \'{REMOTE_WWW}\'\n'
        f'$hostname = \'{HOSTNAME}\'\n'
        f'$proxyUrl = \'{proxy_url}\'\n'
        'if (-not (Test-Path "IIS:\\AppPools\\$poolName")) {\n'
        '    New-WebAppPool -Name $poolName\n'
        '    Set-ItemProperty "IIS:\\AppPools\\$poolName" managedRuntimeVersion ""\n'
        '    Write-Host "Created app pool: $poolName"\n'
        '} else {\n'
        '    Write-Host "App pool exists: $poolName"\n'
        '}\n'
        'if (Get-Website -Name $siteName -ErrorAction SilentlyContinue) {\n'
        '    Remove-Website -Name $siteName\n'
        '    Write-Host "Removed old site"\n'
        '}\n'
        'New-Website -Name $siteName -PhysicalPath $wwwPath -ApplicationPool $poolName -Port 80 -HostHeader $hostname -Force\n'
        'Write-Host "Created site: $siteName"\n'
        'try {\n'
        '    $cert = Get-ChildItem Cert:\\LocalMachine\\WebHosting | Where-Object { $_.Subject -like "*prospergenics*" } | Select-Object -First 1\n'
        '    if (-not $cert) { $cert = Get-ChildItem Cert:\\LocalMachine\\My | Where-Object { $_.Subject -like "*prospergenics*" } | Select-Object -First 1 }\n'
        '    if ($cert) {\n'
        '        New-WebBinding -Name $siteName -Protocol https -Port 443 -HostHeader $hostname -SslFlags 1\n'
        '        $binding = Get-WebBinding -Name $siteName -Protocol https\n'
        '        $binding.AddSslCertificate($cert.Thumbprint, "WebHosting")\n'
        '        Write-Host "HTTPS binding added: $($cert.Thumbprint)"\n'
        '    } else { Write-Host "No cert found - HTTP only" }\n'
        '} catch { Write-Host "HTTPS skipped: $_" }\n'
        '$webConfig = @\'\n'
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<configuration>\n'
        '  <system.webServer>\n'
        '    <rewrite>\n'
        '      <rules>\n'
        '        <rule name="API Proxy" stopProcessing="true">\n'
        '          <match url="^(api|swagger|hubs|hangfire)(.*)" />\n'
        f'          <action type="Rewrite" url="{proxy_url}/{{R:0}}" />\n'
        '        </rule>\n'
        '        <rule name="SPA Fallback" stopProcessing="true">\n'
        '          <match url=".*" />\n'
        '          <conditions>\n'
        '            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />\n'
        '            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />\n'
        '          </conditions>\n'
        '          <action type="Rewrite" url="/index.html" />\n'
        '        </rule>\n'
        '      </rules>\n'
        '    </rewrite>\n'
        '    <staticContent>\n'
        '      <mimeMap fileExtension=".webmanifest" mimeType="application/manifest+json" />\n'
        '    </staticContent>\n'
        '  </system.webServer>\n'
        '</configuration>\n'
        '\'@\n'
        '$webConfigPath = Join-Path $wwwPath "web.config"\n'
        'Set-Content $webConfigPath $webConfig -Encoding UTF8\n'
        'Write-Host "web.config written"\n'
        'Write-Host "IIS setup complete"\n'
    )

# ── Main deploy ──────────────────────────────────────────────────────────────

def main():
    print('=== LeadManager production deploy ===')
    print(f'Target: {HOST} -> {HOSTNAME}')

    # Build locally
    build_backend()
    build_frontend()

    ssh = connect()
    sftp = ssh.open_sftp()

    # 1. Directories
    print('\n[1] Creating directories...')
    for d in [REMOTE_ROOT, REMOTE_WWW, REMOTE_BACKEND, REMOTE_DATA]:
        run(ssh, f'powershell -Command "New-Item -ItemType Directory -Force -Path \'{d}\'"')

    # 2. Stop service
    print('\n[2] Stopping service...')
    run(ssh, f'sc stop {SERVICE}', timeout=15)
    time.sleep(3)

    # 3. Upload backend
    print('\n[3] Uploading backend...')
    upload_dir(sftp, LOCAL_PUBLISH, REMOTE_BACKEND)

    # 4. Upload frontend
    print('\n[4] Uploading frontend...')
    upload_dir(sftp, LOCAL_FRONTEND_OUT, REMOTE_WWW)

    # 5. appsettings.Production.json
    print('\n[5] Writing appsettings.Production.json...')
    settings_path = REMOTE_BACKEND.replace('\\', '/') + '/appsettings.Production.json'
    with sftp.open(settings_path, 'w') as f:
        f.write(get_appsettings())
    print('  written')

    # 6. Windows service
    print('\n[6] Installing Windows service...')
    exe = REMOTE_BACKEND + r'\LeadManager.Api.exe'
    run(ssh, f'sc delete {SERVICE}', timeout=10)
    time.sleep(1)
    run(ssh, f'sc create {SERVICE} binPath= "\\"{exe}\\" --environment Production" start= auto DisplayName= "LeadManager API"', timeout=15)
    run(ssh, f'sc start {SERVICE}', timeout=20)
    time.sleep(5)
    out = run(ssh, f'sc query {SERVICE}')
    if 'RUNNING' in out:
        print(f'  Service {SERVICE}: RUNNING on port {APP_PORT}')
    else:
        print(f'  WARNING: may not have started. Output: {out}')

    # 7. IIS
    print('\n[7] Setting up IIS...')
    iis_script = build_iis_script()
    run(ssh, f'powershell -Command "{iis_script.strip()}"', timeout=30)

    # 8. Quick health check
    print('\n[8] Health check...')
    time.sleep(3)
    out = run(ssh, f'powershell -Command "(Invoke-WebRequest -Uri http://localhost:{APP_PORT}/api/health -UseBasicParsing).StatusCode"', timeout=15)
    if '200' in out:
        print(f'  Health check passed: 200 OK')
    else:
        print(f'  Health check result: {out}')

    sftp.close()
    ssh.close()
    print(f'\n=== Deploy complete ===')
    print(f'  URL: https://{HOSTNAME}')

if __name__ == '__main__':
    main()
