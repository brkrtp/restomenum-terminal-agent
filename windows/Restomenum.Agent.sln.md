# Not: `.sln` dosyası bilerek eklenmedi

`dotnet` SDK bu makinede kurulu değil; `.sln`'i elle yazmak, ilk `dotnet` çalıştıran kişinin
düzelteceği bozuk bir dosya bırakmak olurdu. Proje dosyaları (`.csproj`) hazır ve
`dotnet build`/`dotnet test` doğrudan çalışır. Solution isteyen `dotnet new sln` + `dotnet sln add`
ile saniyeler içinde üretir.
