# RestomenumAgent Windows servisini kurar ya da günceller. YÖNETİCİ PowerShell'de çalıştırın:
#   powershell -ExecutionPolicy Bypass -File servisKur.ps1 -Exe "C:\...\Restomenum.Agent.Host.Windows.exe"
#
# Neden ayrı adım: "çökerse yeniden başlat" servis yöneticisinin KURTARMA EYLEMİYLE olur ve servis
# oluşturulurken kendiliğinden kurulmaz. Kurulum sayfası (terminal adresi, eklenti dizesi, cihazı
# kaldır) ajanı bu eyleme güvenerek yeniden başlatır; eylemlerin hepsi "restart" değilse o işlemleri
# değişiklik yapmadan reddeder (RM-0389).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Ad = "RestomenumAgent"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Exe)) { throw "Ajan bulunamadı: $Exe" }
$Exe = (Resolve-Path $Exe).Path

# sc.exe hata verince PowerShell durmaz; her çağrının çıkış kodu elle denetlenir.
function Sc {
    param([string]$Adim, [string[]]$Arg)
    $cikti = & sc.exe @Arg 2>&1
    $cikti | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "$Adim başarısız (sc.exe çıkış kodu $LASTEXITCODE). Yönetici olarak çalıştırdığınızdan emin olun." }
}

# --pair: her açılışta GMP eşleşmesi (cihaz eşleşmeyi süreç başına tutuyor).
$binPath = "`"$Exe`" --pair"
if (Get-Service $Ad -ErrorAction SilentlyContinue) {
    Sc "Servis yolunu güncelleme" @("config", $Ad, "binPath=", $binPath, "start=", "delayed-auto")
} else {
    Sc "Servis oluşturma" @("create", $Ad, "binPath=", $binPath, "start=", "delayed-auto", "DisplayName=", "Restomenum Terminal Agent")
    Sc "Servis açıklaması" @("description", $Ad, "Restomenum ödeme terminali ajanı (127.0.0.1:7788)")
}

# Kurtarma: 5 sn, 5 sn, 30 sn sonra yeniden başlat; sayaç 1 günde sıfırlanır. Eylemlerin HEPSİ restart.
Sc "Kurtarma eylemi" @("failure", $Ad, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000")
# Süreç hata koduyla kendiliğinden durduğunda da kurtarma eylemi çalışsın.
Sc "Kurtarma bayrağı" @("failureflag", $Ad, "1")

Sc "Kurtarma ayarını okuma" @("qfailure", $Ad)
Write-Host "Tamam. Başlatmak için: Start-Service $Ad"
