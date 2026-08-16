# family-gallery-api

가정용 시놀로지 NAS의 이미지·영상을 가족 구성원에게만 제공하는 읽기 전용 API.

- NAS 원본은 읽기 전용 접근. 쓰기·삭제 경로 없음
- 외부 노출은 Cloudflare Tunnel 단일 경로
- 클라이언트는 Flutter 앱(`family-gallery-app`) 단독
- 모든 사용자 동일한 `viewer` 권한. 역할 구분 없음

## 요구 사항

- .NET SDK 10.0
- (배포) Docker / Docker Compose

## 프로젝트 구조

```
FamilyGallery.slnx
FamilyGallery.Api/
  Program.cs          서비스 등록 / 파이프라인
  Options/            JwtOptions, GalleryOptions
  Data/               AppDbContext, Entities
  Endpoints/          엔드포인트 매핑 확장 메서드
Dockerfile
docker-compose.yml    NAS 배포용
```

## 인증 / 인가

- JWT Bearer 스킴. issuer / audience / 만료 / 서명 키 전부 검증
- 인가 fallback policy 적용. 전 엔드포인트 인증 필수가 기본값
- 익명 허용은 `/health`, 개발용 OpenAPI 문서뿐

## 설정

| 키 | 설명 | 기본값 |
| --- | --- | --- |
| `ConnectionStrings:Default` | SQLite 연결 문자열 | `Data Source=/data/app/family-gallery.db` |
| `Jwt:Issuer` | 토큰 발급자 | `family-gallery-api` |
| `Jwt:Audience` | 토큰 대상 | `family-gallery-app` |
| `Jwt:SigningKey` | HMAC 서명 키 (32자 이상) | **없음. 반드시 외부 주입** |
| `Jwt:AccessTokenMinutes` | access token 유효 시간(분) | `30` |
| `Jwt:RefreshTokenDays` | refresh token 유효 기간(일) | `60` |
| `Gallery:RootPath` | NAS 마운트 경로 (읽기 전용) | `/data/gallery` |

- `Jwt:SigningKey`는 설정 파일에 미포함. 운영은 환경변수 `Jwt__SigningKey`, 로컬은 user-secrets 사용
- `ValidateOnStart` 적용. 필수 설정 누락 시 기동 단계에서 실패

## 로컬 실행

서명 키를 user-secrets에 등록 (최초 1회).

```powershell
cd FamilyGallery.Api
$bytes = New-Object byte[] 48
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($bytes))
```

실행:

```powershell
dotnet run --project FamilyGallery.Api
```

- `http://localhost:5088/health` → `{"status":"ok","version":"..."}`
- `http://localhost:5088/openapi/v1.json` (Development 전용)

Development 환경 기본값은 `Gallery:RootPath` = `./.local/gallery`, SQLite = `./.local/family-gallery.db`. `.local/`은 git 제외 대상. 소스 폴더 `Data/`와 대소문자만 다른 `data/`는 Windows git이 함께 무시하므로 미사용.

## 배포 (Synology NAS)

`docker-compose.yml`과 같은 위치에 `.env` 배치 후 서명 키 지정.

```
JWT_SIGNING_KEY=<32자 이상 랜덤 문자열>
```

```bash
docker compose up -d --build
```

마운트:

- `/volume2/family-gallery` → `/data/gallery` (읽기 전용, 원본)
- `/volume2/docker/family-gallery-api/data` → `/data/app` (SQLite 쓰기 영역)

- 컨테이너는 비root 계정(uid 1654)으로 동작. `/data/app`에 해당 uid의 쓰기 권한 필요
- 외부 노출은 Cloudflare Tunnel 단일 경로. 컨테이너 포트는 호스트 loopback에만 바인딩

## 라이선스

[MIT](./LICENSE)
