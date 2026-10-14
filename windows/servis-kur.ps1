# RestomenumAgent Windows servisini kurar ya da günceller. YÖNETİCİ PowerShell'de çalıştırın:
#   powershell -ExecutionPolicy Bypass -File servis-kur.ps1 -Exe "C:\...\Restomenum.Agent.Host.Windows.exe"
#
# Neden ayrı adım: "çökerse yeniden başlat" servis yöneticisinin KURTARMA EYLEMİYLE olur ve servis
# oluşturulurken kendiliğinden kurulmaz. Kurulum sayfası (terminal adresi, eklenti dizesi, cihazı
# kaldır) ajanı bu eyleme güvenerek yeniden başlatır; eylem yoksa o işlemleri reddeder (RM-0389).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Ad = "RestomenumAgent"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Exe)) { throw "Ajan bulunamadı: $Exe" }

if (-not (Get-Service $Ad -ErrorAction SilentlyContinue)) {
    # --pair: her açılışta GMP eşleşmesi (cihaz eşleşmeyi süreç başına tutuyor).
    sc.exe create $Ad binPath= "`"$Exe`" --pair" start= delayed-auto DisplayName= "Restomenum Terminal Agent" | Write-Host
    sc.exe description $Ad "Restomenum ödeme terminali ajanı (127.0.0.1:7788)" | Write-Host
}

# Kurtarma: 5 sn, 5 sn, 30 sn sonra yeniden başlat; sayaç 1 günde sıfırlanır.
sc.exe failure $Ad reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Write-Host
# Süreç hata koduyla kendiliğinden durduğunda da kurtarma eylemi çalışsın.
sc.exe failureflag $Ad 1 | Write-Host

sc.exe qfailure $Ad | Write-Host
Write-Host "Tamam. Başlatmak için: Start-Service $Ad"
