<#
  Puts the Discord bot token on tharmas (~/threadtender/.env) and restarts the bot.

  Usage:
    .\deploy\set-token.ps1              # prompts for the token (hidden input)
    .\deploy\set-token.ps1 -Token xyz   # or pass it directly

  The token goes over ssh's stdin, never on a command line, so it doesn't show up
  in the server's process list.
#>
param([string]$Token)

$ErrorActionPreference = 'Stop'
$server = 'timerune@tharmas.taglialegne.it'

if (-not $Token) {
    $secure = Read-Host 'Discord bot token' -AsSecureString
    $Token = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
$Token = $Token.Trim()
if ($Token -notmatch '^[A-Za-z0-9._-]+$') {
    throw "That doesn't look like a bot token (expected letters, digits, '.', '_' or '-')."
}

# The remote script holds no secret, so it travels base64-encoded on the command
# line (sidesteps PowerShell/ssh quoting). The token arrives on stdin; PowerShell
# may add a BOM and CRLF when piping, so keep only valid token characters.
$remote = @'
set -e
umask 077
token=$(head -c 500 | tr -cd 'A-Za-z0-9._-')
[ -n "$token" ] || { echo "No token received." >&2; exit 1; }
printf 'DISCORD_TOKEN=%s\n' "$token" > ~/threadtender/.env
systemctl --user restart threadtender.service
echo "Token saved, restarting... (checking again in 10s)"
sleep 10
echo "Service: $(systemctl --user is-active threadtender.service || true)"
echo "--- recent log ---"
# Drop the stack traces a crash dumps into the journal.
journalctl --user -u threadtender.service -n 300 --no-pager -o cat \
  | grep -vE '^(#|Module |Stack trace|ELF |\s+at |\s*$)' | tail -n 8 || true
'@ -replace "`r", ''
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remote))

# Decode to a temp file rather than piping into bash, so bash's stdin stays the token.
$Token | ssh $server "f=`$(mktemp); echo $b64 | base64 -d > `$f; bash `$f; rc=`$?; rm -f `$f; exit `$rc"
