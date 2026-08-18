# family-gallery-api

가정용 시놀로지 NAS의 이미지·영상을 가족 구성원에게만 제공하는 API.

- 조회는 로그인한 사용자 전원, 업로드·삭제는 `editor` 권한 계정만
- 원본 수정 경로 없음. 쓰기는 신규 추가와 삭제뿐이며 삭제는 휴지통 이동으로 처리
- 외부 노출은 Cloudflare Tunnel 단일 경로
- 클라이언트는 Flutter 앱(`family-gallery-app`) 단독

## 요구 사항

- .NET SDK 10.0
- (배포) Docker / Docker Compose

## 프로젝트 구조

```
.config/
  dotnet-tools.json   로컬 도구 매니페스트 (dotnet-ef)
FamilyGallery.slnx
FamilyGallery.Api/
  Program.cs          서비스 등록 / 파이프라인 / DB 초기화
  Options/            JwtOptions, GalleryOptions
  Data/               AppDbContext, Entities
  Migrations/         EF Core 마이그레이션
  Endpoints/          엔드포인트 매핑 확장 메서드
  Services/           토큰 발급
  Cli/                계정 관리 명령
Dockerfile
docker-compose.yml    NAS 배포용
```

## 인증 / 인가

- JWT Bearer 스킴. issuer / audience / 만료 / 서명 키 전부 검증
- 인가 fallback policy 적용. 전 엔드포인트 인증 필수가 기본값
- 익명 허용은 `/health`, 개발용 OpenAPI 문서뿐
- 권한은 `User.Role`의 `Viewer` / `Editor` 2종. 조회 범위는 권한과 무관하게 동일하고, 업로드·삭제만 `Editor`로 제한
- 클레임은 `sub` / `name` / `role` / `jti`. 인바운드 클레임 매핑 비활성화로 발급·검증 이름 일치

| 엔드포인트 | 인증 | 설명 |
| --- | --- | --- |
| `POST /auth/login` | 익명 | 자격 증명 검증 후 토큰 쌍 + 사용자 정보 반환 |
| `POST /auth/refresh` | 익명 | refresh token 회전 발급 |
| `POST /auth/logout` | 필요 | 제시한 refresh token 폐기 |
| `GET /auth/me` | 필요 | 현재 사용자 정보 (DB 조회) |

- refresh token은 원문 미저장. SHA-256 해시로 대조하고 사용 시 회전 발급
- 폐기된 refresh token이 다시 제시되면 탈취로 간주해 해당 사용자의 유효한 세션 전부 차단
- 로그인 실패는 계정 존재 여부와 무관하게 동일 응답. 계정 부재 시에도 해시 검증을 수행해 응답 시간 차이 제거

권한 변경 반영 시점에 주의. access token은 자체 완결적이라 매 요청마다 DB를 조회하지 않음.

- `user add`로 만든 계정은 즉시 로그인 가능
- `user set-role` / `user set-password`는 **이미 발급된 access token에 반영되지 않음**. 최대 `Jwt:AccessTokenMinutes`(기본 30분) 경과 또는 refresh 시점에 반영
- `GET /auth/me`는 DB를 조회하므로 즉시 반영. 인가 판정은 클레임 기준이라 지연

## 사용자 관리

계정 생성·권한 변경은 CLI로만 수행. 관리용 HTTP 엔드포인트 미제공.

```
user list
user add <username> --display-name <표시 이름> [--role viewer|editor]
user set-role <username> <viewer|editor>
user set-password <username>
```

- `--role` 기본값은 `viewer`
- 비밀번호는 인자로 받지 않고 실행 후 표준 입력으로 수신. 셸 히스토리와 프로세스 목록 노출 방지
- 비밀번호는 8자 이상. `set-password` 실행 시 해당 사용자의 유효한 refresh token 전부 폐기
- 성공은 종료 코드 `0`, 실패는 `1`

로컬:

```powershell
dotnet run --project FamilyGallery.Api -- user add dad --display-name "{이름}" --role editor
```

컨테이너 (비밀번호 입력을 위해 `-it` 필요):

```bash
docker compose exec -it api dotnet FamilyGallery.Api.dll user add dad --display-name "{이름}" --role editor
```

## 설정

| 키 | 설명 | 기본값 |
| --- | --- | --- |
| `ConnectionStrings:Default` | SQLite 연결 문자열 | `Data Source=/data/app/family-gallery.db` |
| `Jwt:Issuer` | 토큰 발급자 | `family-gallery-api` |
| `Jwt:Audience` | 토큰 대상 | `family-gallery-app` |
| `Jwt:SigningKey` | HMAC 서명 키 (32자 이상) | **없음. 반드시 외부 주입** |
| `Jwt:AccessTokenMinutes` | access token 유효 시간(분) | `30` |
| `Jwt:RefreshTokenDays` | refresh token 유효 기간(일) | `60` |
| `Gallery:RootPath` | NAS 마운트 경로 (읽기·쓰기) | `/data/gallery` |

- `Jwt:SigningKey`는 설정 파일에 미포함. 운영은 환경변수 `Jwt__SigningKey`, 로컬은 user-secrets 사용
- `ValidateOnStart` 적용. 필수 설정 누락 시 기동 단계에서 실패

## 데이터베이스

- SQLite. 스키마는 EF Core 마이그레이션으로 관리
- 기동 시 마이그레이션 자동 적용. 단일 인스턴스 배포이므로 별도 적용 절차 없음
- DB 파일의 상위 디렉터리는 기동 시 자동 생성. (SQLite가 직접 만들지 않아 최초 실행이 실패하는 것을 막음)
- `journal_mode`는 명시 설정하지 않음. EF Core가 생성하는 SQLite DB는 WAL이 기본값

마이그레이션 추가:

```powershell
dotnet tool restore
dotnet ef migrations add <이름> --project FamilyGallery.Api
```

`dotnet-ef`는 로컬 도구로 버전 고정. 전역 설치 불필요.

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

Development 환경 기본값은 `Gallery:RootPath` = `./.local/gallery`, SQLite = `./.local/family-gallery.db`. `.local/`은 git 제외 대상이며 기동 시 자동 생성된다. 소스 폴더 `Data/`와 대소문자만 다른 `data/`는 Windows git이 함께 무시하므로 미사용.

## 배포 (Synology NAS)

`docker-compose.yml`과 같은 위치에 `.env` 배치 후 서명 키 지정.

```
JWT_SIGNING_KEY=<32자 이상 랜덤 문자열>
```

```bash
docker compose up -d --build
```

마운트:

- `/volume2/family-gallery` → `/data/gallery` (원본, 읽기·쓰기)
- `/volume2/docker/family-gallery-api/data` → `/data/app` (SQLite / 썸네일 캐시 영역)

- 컨테이너는 비root 계정(uid 1654)으로 동작. 두 마운트 모두 해당 uid의 쓰기 권한 필요
- 외부 노출은 Cloudflare Tunnel 단일 경로. 컨테이너 포트는 호스트 loopback에만 바인딩

## 라이선스

[MIT](./LICENSE)
