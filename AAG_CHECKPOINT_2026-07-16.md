# AAG 작업 체크포인트 — 2026-07-16

## 현재 Quest 데이터 상태

- Package ID: `com.UnityTechnologies.com.unity.template.urpblank`
- 앱을 삭제하지 않고 같은 Package ID로 업데이트 설치 중
- Quest runtime manifest 확인 결과:
  - `FP1-S1`: 0/12, unlocked
  - `FP1-S2`: 0/12, unlocked
  - `FP1-S3`: 0/12, unlocked
- 이전 빌드에서 잘못 만든 `red_1`, `red_2`는 UUID로 다시 localize한 뒤 raw Spatial Anchor까지 삭제 완료
- 삭제 후 Quest manifest도 anchors 빈 배열로 저장된 것을 ADB로 확인함

## 해결한 문제

### 손/레이가 떨리며 바닥으로 떨어졌다가 반복 생성되는 문제

- 원인은 Floor/Boundary 설정이 아니었음
- Camera Rig의 Tracking Origin은 이미 `FloorLevel`
- 초기화되지 않은 Meta `ControllerRef.Handedness`를 매 프레임 읽으며 `NullReferenceException` 발생
- 수동 authoring 레이와 `OVRComprehensiveInteractionRig`의 손/레이가 중복 동작
- 수정:
  - 실제 활성 Meta `Controller`만 재사용
  - 필요하면 전용 왼쪽 controller data source 생성
  - 수동 authoring 중 중복 `OVRComprehensiveInteractionRig`/hand roots 비활성화
- Quest에서 레이가 안정적으로 동작하는 것을 확인함

### 이전 빌드 anchor의 UNDO LAST 실패

- 기존 Undo는 현재 실행 세션에서 만든 anchor만 삭제 가능했음
- 수정:
  - manifest UUID로 Quest local anchor storage에서 다시 load/localize
  - raw Spatial Anchor erase 성공 후 manifest와 PlayerPrefs에서 제거
  - 실패 시 manifest를 임의 삭제하지 않고 UUID와 실패 이유 유지/보고
  - 수동 authoring 중 오른쪽 B의 legacy raw-anchor-only erase 차단
- 실제 `red_2`, `red_1` 순서로 erase 성공 확인

### 오른쪽 트리거 생성 객체가 보이지 않는 문제

- 원인: `OVRInput.GetLocalControllerPosition/Rotation` 값을 Unity world pose처럼 바로 사용
- 수정:
  - `OVRCameraRig.trackingSpace.TransformPoint(localPosition)`으로 world position 계산
  - `trackingSpace.rotation * localRotation`으로 world rotation 계산
  - 오른쪽 controller position/orientation tracking이 유효하지 않으면 생성 차단 및 HUD/로그 보고
  - 생성 요청 시 local/world pose와 tracking origin 로그 추가
- C# 컴파일 완료: 오류 0개

## 내일 시작할 지점

1. Unity에서 앱 삭제 없이 `Build And Run`으로 업데이트 설치
2. 오른쪽 트리거를 눌러 컨트롤러 위치에 미저장 anchor prefab이 표시되는지 확인
3. 오른쪽 A를 눌러 저장 후 `FP1-S1`이 `0/12 -> 1/12`로 증가하는지 확인
4. 필요하면 ADB 로그에서 다음 메시지 확인:
   - `[Anchor] Create requested ... local=... world=...`
   - `[Anchor] Created UUID=...`
   - `[AAG Manual Sets Append] ...`
5. 생성/저장 확인 후 FP1 수동 authoring 및 PlacementSetManager 구조 작업으로 복귀

## 관련 파일

- `Assets/Script/AagManualAnchorSetAuthoring.cs`
- `Assets/Script/SpatialAnchorManager.cs`
- `Assets/Script/AnchorLoader.cs`
- Quest runtime manifest: `Application.persistentDataPath/AagManualAnchorSets/fp1_manual_anchor_sets.json`

## 주의

- 앱 삭제/데이터 초기화 금지. 같은 Package ID로 update 설치할 것
- Trigger는 미저장 preview 생성이며 개수는 증가하지 않음
- A 저장 성공 후에만 세트 개수가 증가함
- raw Spatial Anchor erase 성공 전에는 manifest entry를 임의로 제거하지 않음

