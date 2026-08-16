FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# csproj 먼저 복사. 소스 변경 시 restore 레이어 재사용.
COPY FamilyGallery.Api/FamilyGallery.Api.csproj FamilyGallery.Api/
RUN dotnet restore FamilyGallery.Api/FamilyGallery.Api.csproj

COPY FamilyGallery.Api/ FamilyGallery.Api/
RUN dotnet publish FamilyGallery.Api/FamilyGallery.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# 베이스 이미지 기본 비root 계정(uid 1654).
USER $APP_UID

ENTRYPOINT ["dotnet", "FamilyGallery.Api.dll"]
