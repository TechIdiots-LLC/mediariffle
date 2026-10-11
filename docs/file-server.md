# Media on a separate file server

The media usually lives on a Windows (or Samba) file server in the same domain. MediaRiffle only ever needs to
**read** it, so give it a dedicated account that can do nothing else.

The names below are placeholders; use your own:

| Placeholder | Meaning |
| --- | --- |
| `example.lan` / `EXAMPLE` | the Active Directory domain (DNS name / NetBIOS name) |
| `files.example.lan` | the file server |
| `Media` | the share, holding `Shows` and `Movies` folders |
| `media01.example.lan` | the server running MediaRiffle |
| `svc-mediariffle` | the service account for an Ubuntu server, or for IIS without a gMSA |
| `gmsa-mediariffle$` | a group Managed Service Account, for IIS on a domain-joined Windows server |
| `MediaRiffle Readers` | an AD group allowed to read the media |

## 1. In Active Directory

Make a group, so the share's permissions name the role and not an account:

```powershell
New-ADGroup -Name "MediaRiffle Readers" -GroupScope DomainLocal -GroupCategory Security -Path "OU=Groups,DC=example,DC=lan"
```

### For an Ubuntu server: a plain service account

```powershell
New-ADUser -Name "svc-mediariffle" -SamAccountName "svc-mediariffle" `
  -UserPrincipalName "svc-mediariffle@example.lan" -Path "OU=Service Accounts,DC=example,DC=lan" `
  -AccountPassword (Read-Host -AsSecureString "Password") -Enabled $true `
  -PasswordNeverExpires $true -CannotChangePassword $true `
  -Description "MediaRiffle: read-only access to the media share"
Add-ADGroupMember "MediaRiffle Readers" -Members "svc-mediariffle"
```

Use a long random password (it's only ever typed into one file on the media server). Then stop the account being used
for anything else, with a GPO linked to the OU holding service accounts, under *Computer Configuration → Policies →
Windows Settings → Security Settings → Local Policies → User Rights Assignment*:

- **Deny log on locally**: `EXAMPLE\svc-mediariffle`
- **Deny log on through Remote Desktop Services**: `EXAMPLE\svc-mediariffle`

Optionally limit where it can sign in from (*Account* tab → *Log On To…*) to the file server.

### For a Windows/IIS server: a group Managed Service Account (preferred)

A gMSA has a password Windows rotates itself and nobody knows. Once per domain, if there's no KDS root key yet:

```powershell
Add-KdsRootKey -EffectiveImmediately   # usable after AD replication (up to 10 hours)
```

Then:

```powershell
New-ADServiceAccount -Name "gmsa-mediariffle" -DNSHostName "gmsa-mediariffle.example.lan" `
  -PrincipalsAllowedToRetrieveManagedPassword "MEDIA01$"
Add-ADGroupMember "MediaRiffle Readers" -Members "gmsa-mediariffle$"
```

On the IIS server (`MEDIA01`), with the AD PowerShell module:

```powershell
Install-ADServiceAccount gmsa-mediariffle
Test-ADServiceAccount gmsa-mediariffle   # True
```

If you'd rather not use a gMSA, a plain `svc-mediariffle` account as above works for IIS too.

## 2. On the file server

Give the group read access on both the share and the folders (Windows uses the stricter of the two):

```powershell
# Share permission
Grant-SmbShareAccess -Name "Media" -AccountName "EXAMPLE\MediaRiffle Readers" -AccessRight Read -Force

# NTFS: read and list, inherited by everything below
icacls "D:\Media" /grant "EXAMPLE\MediaRiffle Readers:(OI)(CI)RX"
```

Nothing more is needed. MediaRiffle never writes to the media: pictures, the database and transcodes all live in its own
data folder.

Check SMB signing/encryption policy: MediaRiffle works with either, and Ubuntu's `cifs` client supports SMB 3.1.1
encryption (`seal` mount option) if the share requires it.

## 3a. Ubuntu: mount the share

```sh
sudo apt install cifs-utils
sudo install -d -m 0755 /mnt/media
sudo install -m 0600 /dev/null /etc/mediariffle/smb-credentials
sudoedit /etc/mediariffle/smb-credentials
```

```ini
username=svc-mediariffle
password=the long random password
domain=EXAMPLE
```

`/etc/fstab` (one line):

```
//files.example.lan/Media  /mnt/media  cifs  credentials=/etc/mediariffle/smb-credentials,ro,vers=3.1.1,uid=mediariffle,gid=mediariffle,file_mode=0440,dir_mode=0550,nosuid,nodev,noexec,_netdev,x-systemd.automount,x-systemd.mount-timeout=30  0  0
```

- `ro`: mounted read-only on this side as well
- `uid`/`gid`: files appear owned by the local `mediariffle` service user (made in the Ubuntu guide)
- `x-systemd.automount`: mounted on first use, so a file server that's down doesn't hold up booting

```sh
sudo systemctl daemon-reload
sudo mount /mnt/media
ls /mnt/media/Shows | head
```

The library paths are then `/mnt/media/Shows` and `/mnt/media/Movies`. If the share isn't mounted when a scan runs,
MediaRiffle skips that library and removes nothing.

**Without a password file (optional, advanced):** join the server to the domain (`realm join example.lan` with
`sssd`), give `svc-mediariffle` a keytab, and mount with `sec=krb5,cruid=mediariffle` and a `kinit -k` timer. More
moving parts, but no password on disk.

## 3b. Windows / IIS: use UNC paths

Services can't see mapped drive letters, so the libraries point at the share directly. Enter
`\\files.example.lan\Media\Shows` as the folder under **Admin → Libraries** (and use **Check folder** to confirm the
app pool identity can read it), or seed the first libraries in the settings file:

```json
"Libraries": [
  { "Name": "TV Shows", "Kind": "Shows",  "Path": "\\\\files.example.lan\\Media\\Shows" },
  { "Name": "Movies",   "Kind": "Movies", "Path": "\\\\files.example.lan\\Media\\Movies" }
]
```

(JSON doubles each backslash: that's `\\files.example.lan\Media\Shows`.)

The app pool runs as `EXAMPLE\gmsa-mediariffle$` (or `EXAMPLE\svc-mediariffle`); the IIS guide shows how. That identity
reads the share; nothing else on the server needs access to it.
