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

# named volume 마운트 지점 사전 생성 및 비root 계정 소유권 설정.
# 미생성 시 root 소유로 초기화되어 쓰기 불가.
RUN mkdir -p /data/app /data/gallery && chown -R $APP_UID:$APP_UID /data

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# 베이스 이미지 기본 비root 계정(uid 1654).
USER $APP_UID

ENTRYPOINT ["dotnet", "FamilyGallery.Api.dll"]
