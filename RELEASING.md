# GeminiDesk 업데이트 방법

## Gemini 모델만 추가하거나 숨길 때

루트의 `models.json`을 수정하고 `main` 브랜치에 커밋·푸시합니다.
앱은 시작할 때 이 파일을 GitHub에서 확인하고 마지막 성공본을 로컬에 보관합니다.
앱 실행 파일을 다시 배포할 필요는 없습니다.

## 앱 기능을 수정해 새 버전을 배포할 때

1. 변경을 `main`에 커밋·푸시합니다.
2. 이전보다 높은 SemVer 태그를 만들고 푸시합니다.

```powershell
git tag v0.1.1
git push origin v0.1.1
```

GitHub Actions의 `Release GeminiDesk` 작업이 다음 파일을 자동 생성해 GitHub Releases에 게시합니다.

- `GeminiDesk-Setup.exe`: 처음 설치할 때 사용하는 설치 프로그램
- `GeminiDesk-Portable.zip`: 설치 없이 실행하는 포터블 버전
- 전체·델타 업데이트 패키지와 `releases.win.json`: 설치된 앱의 자동 업데이트용 파일

GitHub Actions 화면에서 `Run workflow`를 눌러 버전 번호를 입력해도 같은 배포를 만들 수 있습니다.

## 로컬에서 설치 패키지만 시험할 때

```powershell
.\scripts\Build-Release.ps1 -Version 0.1.1
```

출력은 Git에 포함되지 않는 `artifacts/Releases` 폴더에 생성됩니다.

자동 업데이트는 Velopack으로 만든 Setup 설치판과 포터블 배포판에서 작동합니다. Visual Studio나 `bin` 폴더에서 직접 실행한 개발 빌드는 업데이트를 확인하지 않습니다.
