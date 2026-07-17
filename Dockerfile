# Build multi-estagio: SDK so pra compilar, runtime final so com o ASP.NET
# (imagem final bem menor e sem ferramentas de build expostas em producao).
# .NET 10 ainda esta em preview — trocar pra "10.0" (sem sufixo) quando a
# versao estavel sair, e reconferir se a tag de runtime muda junto.
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY AndressaLeite.slnx .
COPY AndressaLeite/AndressaLeite.csproj AndressaLeite/
COPY AndressaLeite.Tests/AndressaLeite.Tests.csproj AndressaLeite.Tests/
RUN dotnet restore AndressaLeite/AndressaLeite.csproj

COPY AndressaLeite/ AndressaLeite/
RUN dotnet publish AndressaLeite/AndressaLeite.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Usuario nao-root: a imagem base do ASP.NET ja vem com o usuario "app"
# (uid 64198) pronto pra isso.
COPY --from=build /app/publish .
USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AndressaLeite.dll"]
