# Imagem única para todos os serviços (.NET 10). O projeto é escolhido pelo build-arg PROJECT
# (caminho relativo à raiz do repositório, que é o contexto do build):
#   docker build -f deploy/Dockerfile --build-arg PROJECT=Intranet.Gateway/Intranet.Gateway.csproj -t arconet-gateway .
# Base Ubuntu (não alpine): ICU/fuso America/Recife e as bibliotecas nativas do OR-Tools exigem glibc.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN test -n "$PROJECT" || (echo "Informe --build-arg PROJECT=<caminho do .csproj>" && exit 1)
RUN dotnet publish "$PROJECT" -c Release -o /app/publish -p:UseAppHost=false \
    && echo "$(basename "$PROJECT" .csproj).dll" > /app/publish/.entrypoint

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ENV TZ=America/Recife \
    ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app/publish .
# Serilog grava em {ContentRoot}/logs: a pasta precisa ser do usuário sem privilégios da imagem
RUN mkdir -p /app/logs && chown "$APP_UID" /app/logs
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$(cat /app/.entrypoint)\""]
