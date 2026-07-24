# FP2 하이브리드 AprilTag 정렬 이식 실행서

작성 기준일: 2026-07-25

기준 구현: `MainTest_FP1.unity`의 Room3 하이브리드 태그 평행이동 정렬

대상: `MainTest_FP2.unity` 및 FP2 전용 공간 데이터

## 1. 문서 목적과 현재 상태

이 문서는 오늘 FP1 Room3에서 현장 검증한 하이브리드 태그 정렬을 FP2에
안전하게 이식하기 위한 단일 기준 문서다. 코드 복사 순서뿐 아니라 태그 생성,
Unity 씬 저작, Quest 거리 검증, 현장 적용, 세션 운영, 로그 검토, 실패 조건과
저녁에 추가할 공간 변화 감시까지 포함한다.

현재 상태는 다음과 같다.

- FP1: Room3 태그 검출, Preview, 명시적 Apply, 콘텐츠 평행이동과 현장 미세조정이 동작한다.
- FP2: 씬·설정·로그 namespace는 분리됐지만 실제 Room UUID가 아직 등록되지 않았다.
- FP2 하이브리드 정렬: 아직 구현하지 않는다. 이 문서 순서대로 공간 등록 후 이식한다.
- 태그를 이용한 세션 중 읽기 전용 잔차 감시와 자동 중단: 설계만 확정됐고 구현은 보류한다.

FP2의 `rooms`가 비어 있는 동안 시작 검증 실패는 정상적인 fail-closed 동작이다.
FP1 Room UUID나 태그 기준 좌표를 임시로 넣어 우회하지 않는다.

## 2. 변경하지 않을 핵심 원칙

1. MRUK World Lock은 계속 켠다.
2. AprilTag 보정은 세션 시작 전에 한 번만 적용한다.
3. `OVRCameraRig`, `TrackingSpace`, `MRUK`, `MRUKRoom`, `MRUKAnchor`는 이동하지 않는다.
4. 보정 대상은 앱이 소유한 실험 콘텐츠 루트뿐이다.
5. 태그 검출 후 자동 Apply하지 않는다. 연구자가 Preview를 검토한 뒤 명시적으로 적용한다.
6. 실험 중 공간 변화가 의심되어도 자동 재정렬, MRUK 재로드, 객체 재스폰을 하지 않는다.
7. 검증 실패 시 잘못된 위치로 계속 진행하지 않고 시작을 차단하거나 세션을 중단한다.
8. 태그의 회전은 현재 보정에 사용하지 않는다. 현 구현은 **평행이동만** 계산한다.

정렬 계산은 다음과 같다.

```text
태그 보정 = 안정화된 현실 태그 world 위치 - MRUK 기준 태그 world 위치
콘텐츠 보정 = 태그 보정 + 현장 콘텐츠 미세조정
최종 위치 = 보정 전 콘텐츠 world 위치 + 콘텐츠 보정
```

태그 기준 시각화는 현실 태그 중심으로 이동하지만, 콘텐츠 미세조정은 적용하지 않는다.
따라서 빨간 큐브는 태그 위치 확인용이고 돌·석탑·incidental object는 큐브와 다른
추가 오프셋을 받을 수 있다.

## 3. FP1에서 확인된 구현 기준값

### 3.1 태그와 카메라

| 항목 | 확정값 |
|---|---:|
| AprilTag family | `tagStandard41h12` |
| FP1 tag ID | `0` |
| 검출기에 넣는 `tagSizeMeters` | `0.095 m` |
| 전체 인쇄 AprilTag bitmap | `0.171 m` |
| module 크기 | `0.019 m` |
| 권위 pose | AprilTag pose |
| QR 역할 | payload 교차검증 전용; pose 평균 금지 |
| FP1 QR payload | `AAG-FP1-ZONE:room3` |
| 카메라 | Quest 왼쪽 Passthrough Camera |
| 요청 해상도 / 최대 FPS | `1280 x 960 / 30` |
| detector decimation | `2` |
| 처리 주기 | `0.10 s` |

`0.095 m`는 인쇄물 외곽 171 mm가 아니라 AprilTag 검출 corner 사이의
검정/흰색 경계 폭이다. 171 mm를 검출 크기로 넣으면 거리 추정이 약 1.8배로
틀어질 수 있다.

### 3.2 안정 Preview와 Apply 조건

| 항목 | 확정값 |
|---|---:|
| 표본 창 | `2.0 s` |
| 최소 표본 | `12` |
| 최근 검출 요구 | `0.5 s` 이내 |
| 최대 안정 jitter | `0.045 m` |
| 시작 전 태그 freshness | `12 s` |
| Apply 입력 | 양쪽 thumbstick `1.5 s` 유지 |
| 최대 보정 크기 | `40 m` |
| 최대 수직 보정 | `0.5 m` |
| FP1 현재 `horizontalOnly` | `false`, 즉 XYZ 평행이동 |

FP1의 현장 콘텐츠 미세조정은 다음과 같다.

- 태그가 붙은 벽을 따라 Room3 뒤쪽으로 `0.50 m`
- 태그 벽에서 방 안쪽으로 `0.40 m`
- 높이 추가 조정 `0.00 m`

이 값은 태그 회전으로 만든 수평 기준축에 적용되므로 태그/방의 yaw가 바뀌어도
월드 X/Z를 직접 추측하지 않는다. 단, 이 `0.50/0.40 m`는 **FP1 Room3에서 얻은
현장 고유값**이다. FP2에서 숫자를 그대로 복사하지 말고 FP2 기준 벽에서 다시
측정한다.

### 3.3 FP1 기준 하이어라키

```text
/AAG_Room3_Tag_Alignment
└─ MRUKAlignmentRoot
   └─ Room3_TagReference
      └─ Room3_TagReference_RedCube_10cm
```

- 빨간 큐브 크기: `0.10 x 0.10 x 0.10 m`
- 태그 원점에서 로컬 `+Z` 방향으로 `0.08 m`
- collider 없음
- `Room3_TagReference`의 로컬 `+Z`는 태그 벽에서 방 안쪽을 향한다.
- 기준 Transform 원점은 171 mm 인쇄판이나 QR 중심이 아니라 95 mm AprilTag
  검출 사각형 중심이다.

### 3.4 현재 이미 동작하는 안전장치

- 정확한 Room/floor UUID, MRUK 초기화, World Lock, VR input focus를 시작 전에 검사한다.
- 태그 기준점 미확정, UUID 오류, floor anchor 누락, 불안정 Preview, 오래된 검출은 시작을 막는다.
- `AagFixedSpaceOffset`은 카메라 rig와 MRUK 계층을 등록 대상으로 거부한다.
- 같은 루트를 중복 등록하거나 부모·자식에 보정을 이중 적용하지 않는다.
- 보정은 baseline에 대한 절대 대입이라 같은 값을 다시 적용해도 누적되지 않는다.
- 세션 중 `TrackingSpace`가 한 프레임에 `0.75 m` 초과 또는 `10°` 초과로
  변하면 직전 pose로 되돌리고 `spatial_integrity_abort`로 세션을 종료한다.
- 앱 pause/Quest 로비 진입 시 실행 중 세션을 종료한다.
- JSONL에 `fixed_space_offset_applied`, `spatial_integrity_breach`,
  `application_pause`, `session_end` 등의 시스템 이벤트를 기록한다.

## 4. FP2에 그대로 복사할 것과 새로 측정할 것

### 그대로 재사용할 구현 정책

- `jp.keijiro.apriltag` detector와 Passthrough Camera pose 변환 방식
- `tagSizeMeters = 0.095f`인 동일 물리 규격
- 2초 median, 최소 12표본, jitter 4.5 cm 조건
- Preview Only → 현장 확인 → 명시적 Apply 순서
- 콘텐츠 루트만 이동하고 MRUK/TrackingSpace는 이동하지 않는 정책
- 40 m 전체 보정 제한, 0.5 m 수직 제한
- 0.75 m/10° 단일 프레임 추적 점프 중단
- 앱 pause/Quest 로비 진입 중단
- JSONL 단일 writer와 공간별 로그 namespace

### FP2에서 반드시 새로 만들거나 측정할 값

- FP2 실제 Room UUID 목록과 각 floor anchor UUID
- FP2 시작 방 ID와 태그를 붙일 대응 벽
- FP2 export floor world pose와 태그의 floor-local pose
- FP2 콘텐츠의 벽 방향 미세조정과 벽 이격 거리
- FP2 전용 AprilTag ID와 QR payload
- FP2 S1/S2/S3 돌, 4개 석탑, incidental object의 UUID/room-local 배치
- FP2 태그 인쇄물과 manifest
- FP2 현장 거리/위치/재시작 재현성 결과

### 절대 복사하지 않을 FP1 고유값

- Room3 UUID `0d537c33-3e47-2606-3ea9-897c2bc9f1ce`
- Room3 floor anchor UUID `62768480-a2ae-7bb5-3d77-e8de8355e2cb`
- 2026-07-21 FP1 Room3 export frame
- FP1 `floorLocalPosition`, `floorLocalRotation`
- FP1 QR payload `AAG-FP1-ZONE:room3`
- FP1 현장 미세조정 `0.50/0.40 m`
- FP1 태그 ID `0`을 검토 없이 재사용하는 것

두 인쇄판이 같은 건물이나 실험 운영 중 동시에 존재할 수 있으면 FP2에는 다른
AprilTag ID를 배정한다. 권장 첫 후보는 ID `1`이지만 실제 코드 지원과 생성기
테스트를 통과한 뒤 확정한다. QR payload는 `AAG-FP2-ZONE:<start-room-id>` 형식으로
공간 namespace를 분리한다.

## 5. FP2 이식 실행 순서

다음 순서를 바꾸지 않는다. 각 단계의 산출물이 없으면 다음 단계로 넘어가지 않는다.

### 단계 0 — 소스와 현장 기준 동결

1. FP1 안정 태그 `fp1-stable-20260724`와 FP2 분리 체크포인트 `9be83cb`를 보존한다.
2. 하이브리드 태그 작업은 별도 Git 브랜치에서 수행한다.
3. FP2 Space Setup을 다시 만들 가능성이 있으면 먼저 최종 스캔을 확정한다.
4. 태그를 붙일 벽과 태그 중심 높이·문/모서리 기준 거리를 종이에 기록한다.
5. 물리 태그는 최종 측정 후 움직이지 못하게 평평하고 단단하게 고정한다.

### 단계 1 — FP2 공간 등록

1. Unity 메뉴 `AAG > Build FP2 Room UUID Scanner APK`를 빌드한다.
2. FP2 공간에서 Quest Space Setup과 World Lock 설정을 확인한다.
3. 스캐너를 실행하고 `AagRoomExports/FP2/fp2_room_coordinates_*.json`을 가져온다.
4. `FP2ExperimentConfig.asset`의 `rooms`에 `roomId`, `roomUuid`, `displayName`,
   `tieOrder`를 입력한다.
5. `startRoomId`가 태그를 설치할 실제 시작 방인지 확인한다.
6. 시작 방의 정확한 floor anchor UUID와 export floor pose를 별도 기록한다.

통과 조건: FP2 설정에 FP1 UUID가 없고, 공간 검증기가 예상 방 전체를 정확히 찾는다.

### 단계 2 — FP2 태그 생성기 일반화와 인쇄물 생성

현재 `Tools/FiducialTags/generate_hybrid_tags.py`는 출력 파일명과 runtime manifest
경로 일부가 FP1로 고정되어 있다. 설정 JSON만 FP2로 바꿔 실행하지 말고 먼저 다음을
일반화한다.

- `spaceId`를 파일명과 runtime manifest 이름에 반영
- `roomId`, `aprilTagId`, `qrPayload`를 입력 설정에서 읽기
- FP1/FP2 출력이 서로 덮어쓰이지 않게 공간별 output 경로 사용
- ID별 공식 `tagStandard41h12` matrix를 검증하거나 지원하지 않는 ID는 fail-closed
- 테스트에 FP2 manifest, 파일명, QR digest, SVG 물리 크기 검증 추가

FP2 설정 예시는 다음과 같다. ID는 코드·생성기 지원 확인 후 확정한다.

```json
{
  "spaceId": "FP2",
  "referenceTag": {
    "roomId": "<FP2 시작 방 ID>",
    "aprilTagId": 1,
    "qrPayload": "AAG-FP2-ZONE:<FP2 시작 방 ID>"
  }
}
```

생성 후 확인할 산출물:

- A4 landscape 하이브리드 SVG
- AprilTag-only SVG
- QR-only SVG
- FP2 JSON/CSV manifest
- `Assets/StreamingAssets/AAG/fp2_hybrid_tag_manifest.json`

SVG를 A4 landscape, `100% / Actual Size`, matte white 용지로 인쇄한다. `Fit to page`와
crop을 끄고 TOP 화살표가 위를 향하게 한다. 자로 module 19 mm, 전체 bitmap 171 mm,
검출 corner span 95 mm를 확인한다. JPG/스크린샷은 치수 인쇄 원본으로 사용하지 않는다.

### 단계 3 — 런타임 코드를 공간 중립으로 변경

FP1 전용 이름과 상수를 그대로 복제하지 말고 다음 컴포넌트를 FP1/FP2 설정형으로
일반화하는 것을 우선한다.

| 현재 FP1 파일 | FP2 이식 작업 |
|---|---|
| `AagRoom3TagReference.cs` | space/room/floor UUID, export floor pose, 태그 ID를 직렬화된 설정으로 이동 |
| `AagRoom3ReferencePreview.cs` | FP2 export JSON 기반 preview 또는 공간별 preview 컴포넌트 생성 |
| `AagAprilTagTranslationAligner.cs` | `Room3`, ID 0, HUD 문구를 활성 공간/시작 방 기준으로 일반화 |
| `AagRoom3AprilTagSetup.cs` | FP1/FP2 메뉴와 대상 씬·이름·기준값을 매개변수화 |
| `AagRoom3AprilTagSceneSetupTests.cs` | FP1 회귀 테스트 유지 + FP2 배선/UUID 격리 테스트 추가 |
| `AagAprilTagRangeValidator*` | 선택된 ID와 tag size가 manifest와 일치하도록 설정형으로 변경 |
| `ExperimentMain.cs` | 활성 공간의 aligner만 참조하고 로그 source를 FP1 고정 문자열 없이 기록 |

권장 FP2 하이어라키:

```text
/AAG_FP2_Tag_Alignment
└─ MRUKAlignmentRoot
   └─ FP2_TagReference
      └─ FP2_TagReference_RedCube_10cm
```

FP1/FP2가 동일 코드에서 동작하더라도 태그 reference, manifest, UUID, scene object와
테스트 기대값은 공간별로 분리한다.

### 단계 4 — FP2 씬 기준점 배치

1. `MainTest_FP2.unity`를 연다.
2. FP2 export floor/wall/door preview를 표시한다.
3. `FP2_TagReference` 원점을 물리 AprilTag 95 mm 검출 사각형 중심에 대응시킨다.
4. 로컬 `+Z`가 태그 벽에서 방 안쪽을 향하도록 회전한다.
5. 10 cm 빨간 큐브가 태그 앞쪽에 보이는지 확인한다.
6. 현재 Transform으로 floor-local pose를 Capture한다.
7. UUID·벽·높이·방향을 2인 교차검토한 마지막 순간에만
   `Reference Placement Confirmed`를 켠다.
8. 확정 후 태그 reference Transform을 다시 움직이지 않는다.

주의: 현재 `AagRoom3TagReference.OnValidate()`는 Edit Mode에서 Transform이 바뀌면
floor-local pose를 자동 재캡처한다. 실수로 기준점을 움직인 뒤 저장하면 그 위치가 새
기준이 된다. FP2 일반화 때는 명시적 Capture만 허용하거나, 확정된 reference 변경 시
경고·확인 절차를 추가하는 것이 안전하다.

### 단계 5 — Preview Only 검증 빌드

초기 FP2 씬 설정:

```text
Preview Only = true
Allow Quest Controller Apply = false
Require Applied Alignment Before Session = true
Horizontal Only = false
Content fine tune = (0, 0)
```

이 상태에서는 보정값만 보고 적용은 금지한다. HUD에서 다음을 확인한다.

- `Detected median`: 현실 태그의 안정화된 world 위치
- `MRUK reference`: FP2 floor-local 기준에서 복원한 태그 위치
- `Tag offset`: 두 위치의 차이
- `Jitter`: 2초 표본의 최대 편차
- `Content fine`: 아직 0
- `Content offset`: 현재는 Tag offset과 동일

Preview가 `STABLE`이어도 빨간 큐브가 실제 태그와 맞지 않으면 기준점/크기/좌표 변환을
수정한다. 콘텐츠 미세조정으로 태그 기준 오류를 숨기지 않는다.

### 단계 6 — 0.5 m / 1.0 m / 1.5 m 거리 검증

전용 validator APK에서 태그 표면부터 **사용 중인 왼쪽 RGB 카메라**까지 거리를 잰다.
발끝이나 HMD 중심이 아니라 카메라 위치를 기준으로 한다.

| 실제 거리 | 매우 양호 | 1차 사용 가능 |
|---:|---:|---:|
| 0.50 m | 약 0.485–0.515 m | 약 0.47–0.53 m |
| 1.00 m | 0.97–1.03 m | 0.94–1.06 m |
| 1.50 m | 약 1.455–1.545 m | 약 1.41–1.59 m |

정면에서는 camera-space `Z`와 직선거리 `magnitude`가 거의 같아야 한다. 모든 거리에서
같은 비율로 틀리면 tag size를, 거리에 따라 비선형으로 틀리면 camera intrinsics와
영상 좌표 변환을 검사한다.

### 단계 7 — 적용 활성화와 현장 콘텐츠 미세조정

거리와 기준 큐브 검증을 통과한 뒤에만:

```text
Preview Only = false
Allow Quest Controller Apply = true
```

1. 양쪽 thumbstick을 1.5초 눌러 한 번 Apply한다.
2. 빨간 큐브가 실제 태그 중심에 고정되는지 확인한다.
3. 머리를 좌우·상하로 움직여 큐브가 벽에 붙어 보이고 시점과 함께 떠다니지 않는지 확인한다.
4. 콘텐츠가 전체적으로 벽을 따라 앞/뒤로 틀렸으면 `alongWallBackMeters`만 조정한다.
5. 콘텐츠가 태그 벽에 너무 붙었거나 멀면 `wallClearanceMeters`만 조정한다.
6. 높이는 위 두 값으로 바꾸지 않는다. 높이 오류는 태그 기준점/수직 보정 문제로 분리한다.
7. 한 번에 한 축, 0.10–0.20 m 단위로 바꾸고 새 APK에서 재확인한다.

Unity world X/Z를 사진만 보고 직접 더하거나 빼지 않는다. 태그의 로컬 기준축을 사용해야
다른 공간 yaw에서도 같은 의미를 유지한다.

### 단계 8 — 콘텐츠 등록과 스폰 검증

`AagFixedSpaceOffset.RegisteredRootCount`가 FP2에서 기대한 실제 개수와 일치해야 한다.
FP1의 12개 돌 + 4개 석탑 + 5개 incidental = 21개는 FP1 참고값일 뿐이다.
FP2 객체 수가 다르면 하드코딩하지 말고 FP2 config/manifest에서 기대값을 계산한다.

각 등록 루트에 대해 다음을 검증한다.

- 보정 전 baseline이 콘텐츠 스폰 직후의 올바른 world 위치인가
- 태그 보정과 content fine tune이 정확히 한 번 적용됐는가
- 부모와 자식이 동시에 등록되어 이중 이동하지 않는가
- 돌, 석탑, incidental object가 모두 같은 콘텐츠 보정을 받는가
- 빨간 태그 큐브는 content fine tune을 받지 않는가
- MRUK/Room/Floor/Camera/TrackingSpace Transform은 Apply 전후 동일한가

최소 현장 매트릭스:

- 세트: FP2-S1, FP2-S2, FP2-S3 각각 한 번 이상
- 안내: AAG, VG, NG에서 동일 공간 배치가 유지되는지 확인
- 시작 → Apply → Play → 종료 → 앱 완전 종료 → 재실행을 최소 3회 반복
- 시작 방뿐 아니라 모든 사용 방을 걸어 다니며 위치·충돌·raycast·delivery zone 확인

### 단계 9 — 참가자 세션 운영 SOP

현재 보류 중인 지속 감시가 구현되기 전에는 세션마다 다음 순서를 사용한다.

1. 이전 앱을 완전히 종료한다. Quest 로비에서 돌아와 그대로 이어서 쓰지 않는다.
2. 앱을 새로 실행하고 MRUK 로드와 World Lock 활성화를 확인한다.
3. 태그를 정면에서 안정적으로 비춘다.
4. `STABLE / ARMED`와 jitter를 확인한다.
5. 양쪽 thumbstick 1.5초로 Apply한다.
6. `APPLIED`와 빨간 큐브의 실제 태그 일치를 확인한다.
7. 콘텐츠 기준물 1개 이상이 기대 위치인지 확인한다.
8. 참가자·세트·안내 조건을 확인한 뒤 `A`로 Play한다.
9. 세션 중 Quest 로비 진입, 앱 pause, 큰 공간 점프가 발생하면 해당 세션을 무효 처리한다.
10. 종료 후 다음 참가자/세션 전에 앱을 완전히 재실행하고 다시 정렬한다.

앱 종료 → 새 태그 정렬 → Play 순서는 이전 `isApplied`와 Transform baseline이 다음
세션에 남을 가능성을 줄이므로, 현재 구조에서는 세션만 연속 재사용하는 것보다 안전하다.

### 단계 10 — 로그 검토와 승인

FP2 로그 위치:

```text
Application.persistentDataPath/FP2Logs/<session-id>/session.jsonl
```

세션별 필수 확인:

- `fixed_space_offset_applied`가 정확히 한 번 존재
- `source`가 활성 FP2 AprilTag 정렬임을 표시
- `roots`가 FP2 기대 콘텐츠 수와 일치
- 적용 offset이 finite이며 승인한 범위 내
- `horizontalOnly`가 현장 승인값과 일치
- `spatial_integrity_breach`, `application_pause`, 비정상 `session_end`가 없음
- 돌/석탑/incidental 로드 이벤트의 최종 위치가 보정 후 좌표와 일치
- 세트와 안내 조건 변경이 공간 보정값을 바꾸지 않음

승인 전 3회 이상 재실행한 `Tag offset`, `Content offset`, jitter를 비교한다. 오프셋이
실행마다 수 cm 이상 체계적으로 바뀌면 태그 부착, 조명, 카메라 pose, MRUK relocalization을
조사하고 참가자 실험으로 넘어가지 않는다.

## 6. 빌드 전 검정 목록

아래 항목은 하나라도 실패하면 빌드/세션을 진행하지 않는다.

### 공간 격리

- [ ] `MainTest_FP2.unity`가 `FP2ExperimentConfig.asset`을 참조한다.
- [ ] 활성 `spaceId`, `floorPlanId`, storage namespace가 FP2다.
- [ ] FP2 `rooms`가 비어 있지 않고 실제 export와 일치한다.
- [ ] FP2 설정·scene·manifest에 FP1 Room/floor UUID가 없다.
- [ ] FP2 로그, PlayerPrefs, 수동 앵커, room-local 파일 경로가 FP1과 분리돼 있다.

### 태그 자산

- [ ] FP2 tag ID가 코드, 인쇄 SVG, manifest, validator에서 동일하다.
- [ ] QR payload가 정확히 `AAG-FP2-ZONE:<start-room-id>`다.
- [ ] `tagSizeMeters`는 `0.095`, 인쇄 bitmap은 `0.171 m`다.
- [ ] SVG 100% 인쇄 후 module 19 mm와 두 기준 폭을 자로 확인했다.
- [ ] 태그는 평평하고 단단하며 세션 중 움직이지 않는다.

### 씬 배선

- [ ] FP2 reference의 Room/floor UUID가 실제 FP2 시작 방과 일치한다.
- [ ] floor-local pose를 최종 Transform에서 Capture했다.
- [ ] reference `+Z`가 방 안쪽을 향한다.
- [ ] 빨간 큐브는 10 cm, local `+Z 0.08 m`, collider 없음이다.
- [ ] `ExperimentMain`, aligner, `AagFixedSpaceOffset` 참조가 모두 연결돼 있다.
- [ ] scene content roots에는 MRUK/Camera/TrackingSpace가 없다.
- [ ] `Reference Placement Confirmed`는 현장 교차검토 후에만 켜졌다.

### 코드와 테스트

- [ ] C# runtime/editor assembly가 오류 없이 컴파일된다.
- [ ] FP1 하이브리드 scene 회귀 테스트가 계속 통과한다.
- [ ] FP2 scene 배선, UUID 격리, manifest 일치 테스트가 통과한다.
- [ ] 태그 생성기 단위 테스트와 FP2 출력 테스트가 통과한다.
- [ ] FP2 Build Profile에 `MainTest_FP2.unity`만 포함된다.
- [ ] 참가자 빌드에서 manual authoring이 꺼져 있다.

## 7. 현장 시작 검토 목록

- [ ] Quest가 올바른 FP2 Space Setup을 불러왔다.
- [ ] MRUK World Lock이 켜져 있다.
- [ ] VR input focus가 있다.
- [ ] Passthrough Camera 권한이 허용됐다.
- [ ] 태그 ID와 QR payload가 FP2 값이다.
- [ ] 2초 median 표본이 12개 이상이다.
- [ ] jitter가 4.5 cm 이하이며 권장은 2 cm 이하다.
- [ ] Preview offset이 finite이고 예상 규모다.
- [ ] 수직 보정 절대값이 0.5 m 이하다.
- [ ] 빨간 큐브가 실제 태그와 일치한다.
- [ ] Apply 후 큐브가 머리 움직임에 따라 미끄러지지 않는다.
- [ ] 콘텐츠 기준점과 모든 객체 종류가 올바른 위치다.
- [ ] Apply 후에만 Play를 눌렀다.

## 8. 세션 중 금지 목록과 즉시 중단 조건

### 실험 중 금지

- `alignmentOffset` 자동 재계산
- 콘텐츠 전체 자동 이동 또는 스냅
- MRUK 재로드, Room 재스폰, Space Setup 변경
- 태그 기준 Transform 수동 이동
- Quest 로비 진입 후 같은 참가자 세션 계속 진행
- 위치가 이상한 개별 객체를 현장에서 임의로 끌어 맞춘 뒤 유효 세션으로 기록

### 현재 구현된 즉시 중단

- 한 프레임 `TrackingSpace` 위치 변화 `> 0.75 m`
- 한 프레임 `TrackingSpace` 회전 변화 `> 10°`
- 실행 중 앱 pause/Quest 로비 진입

경계값이 코드에서 `>`이므로 정확히 0.75 m 또는 10°와 같은 경우가 아니라 초과 시
발동한다. 연구 문서에는 보수적으로 `0.75 m 이상 / 10° 이상`으로 운영해도 된다.

## 9. 저녁에 구현할 읽기 전용 공간 변화 감시

이 절은 **아직 구현되지 않은 확정 요구사항**이다. FP1에 먼저 적용·검증한 뒤 같은
공간 중립 구현을 FP2에도 적용한다.

### 감시 기준

Apply 직후 저장할 값:

```text
lockedExpectedTagWorldPosition = 적용 직후 안정화된 현실 태그 world 위치
lockedAlignmentOffset = 실제 적용한 콘텐츠 보정
```

태그가 안정 검출될 때의 잔차:

```text
residual = distance(stableDetectedTagWorldPosition,
                    lockedExpectedTagWorldPosition)
```

중단 규칙:

- 안정된 태그 잔차 `>= 0.15 m`: 즉시 중단
- 안정된 태그 잔차 `>= 0.05 m`가 `1.0 s` 이상 지속: 중단
- 한 프레임 위치 변화 `>= 0.75 m`: 기존 즉시 중단 유지
- 한 프레임 회전 변화 `>= 10°`: 기존 즉시 중단 유지
- 앱 pause, focus 상실, Quest 로비 진입, tracking 상태 상실: 중단

태그가 보이지 않거나 안정 표본 조건을 충족하지 못한 것은 오류가 아니다. 잔차 검사를
보류하고 세션은 계속한다. 태그 근처로 돌아와 안정 검출될 때 다시 검사한다. 다른 방에서의
tracking-space 점프는 기존 TrackingSpace 감시와 tracking 상태 감시가 담당한다.

감시 중에는 보정값을 절대 갱신하지 않는다. 중단 시:

1. 실험 타이머와 상호작용을 정지한다.
2. 현재 세션을 무효 처리한다.
3. 자동으로 객체를 움직이지 않는다.
4. JSONL에 원인과 측정값을 기록하고 즉시 flush한다.
5. 앱을 재실행한 새 세션에서 다시 태그 정렬한다.

권장 종료 원인 enum:

```csharp
public enum SessionAbortReason
{
    None,
    InstantPositionJump,
    InstantRotationJump,
    SustainedTagResidual,
    CriticalTagResidual,
    TrackingLost,
    ApplicationPaused,
    ApplicationFocusLost
}
```

필수 로그 필드:

```text
Timestamp / Session ID / Space ID / Current Room
Failure Reason
Position Step / Rotation Step
AprilTag Residual / Residual Duration / Tag Stable / Tag Visible
Tracking State / Application Pause / Application Focus
Last Valid Head Pose
Detected Tag Pose / Locked Expected Tag Pose
Applied Tag Offset / Content Fine Tune / Applied Content Offset
Registered Root Count
```

CSV를 별도로 여는 대신 기존 `LoggingManager`의 `session.jsonl` 단일 writer를 사용한다.

### 다음 세션 초기화 요구사항

새 앱 실행 또는 명시적 새 정렬 주기에서 다음을 모두 초기화한다.

```text
isApplied = false
appliedOffset = Vector3.zero
태그 표본과 잔차 타이머 = 0
lockedExpectedTagWorldPosition 폐기
누적/직전 tracking 상태 초기화
종료 원인 초기화
등록 콘텐츠를 원래 baseline으로 복원 후 등록 해제
기존 세션 객체 제거 또는 초기 상태 복구
MRUK 로드 완료 후 새 태그 Preview 시작
```

중요: `ExperimentMain.PrepareForNewSession()`에서 무조건 aligner를 reset하면 연구자가
Apply한 직후 Play를 누를 때 정렬까지 지워질 수 있다. 초기화는 `A` 입력 내부가 아니라
앱 시작, 명시적 재정렬 명령 또는 이전 세션 종료 후 다음 대기 상태로 전환되는 경계에
정확히 배치하고 테스트한다.

## 10. 위험요소와 대응

| 위험 | 현재 영향 | 대응 |
|---|---|---|
| 평행이동만 보정 | 공간 yaw/회전 오차는 남음 | 여러 방 기준점 진단에서 회전 증거가 있으면 적용 금지 |
| 한 개 태그 기준 | 태그 설치/검출 오류가 전체 콘텐츠에 전파 | 물리 측정, 3거리 검증, red cube, 2인 확인 |
| FP1 fine tune 복사 | FP2 벽 방향/가구 배치와 불일치 | FP2에서 두 수평축을 독립 재측정 |
| MRUK 방별 비강체 변화 | 한 전역 translation으로 모든 방이 맞지 않음 | 여러 방 기준점 검증; 방마다 잔차가 다르면 중단 |
| 느린 누적 drift | 0.75 m/10° 한 프레임 guard가 못 잡음 | 5 cm/1초 태그 잔차 감시 추가 |
| 태그 미검출 | 다른 방에서 정상적으로 태그가 안 보임 | 미검출은 오류로 처리하지 않음 |
| 앱 pause/로비 | 복귀 시 world frame 재로컬라이즈 가능 | 현재처럼 즉시 세션 종료, 앱 재실행 |
| `isApplied` 재사용 | 다음 세션이 이전 보정을 신뢰할 수 있음 | 세션별 앱 완전 종료 SOP + 명시적 reset 구현 |
| OnValidate 자동 캡처 | 실수로 움직인 기준점이 새 기준으로 저장 | 확정 후 편집 금지, 명시적 Capture/경고로 개선 |
| tag size 혼동 | 거리와 전체 보정 scale 오류 | detector는 95 mm, 인쇄 외곽은 171 mm로 분리 |
| 부모/자식 이중 등록 | 콘텐츠가 두 배 이동 | 등록 거부 로직과 root count/최종 위치 테스트 |
| 일부 객체 미등록 | 특정 종류만 보정 전 위치에 남음 | 돌·석탑·incidental 종류별 검정 목록 사용 |
| FP1/FP2 namespace 혼합 | 잘못된 방/앵커/로그 사용 | 공간 격리 Editor 테스트와 manifest 검증 |

## 11. 승인 기준(Definition of Done)

FP2 이식 완료는 다음을 모두 만족할 때만 선언한다.

- FP2 실제 Room/floor UUID와 태그 기준 floor-local pose가 등록됐다.
- FP2 전용 태그 ID/QR payload/manifest/인쇄물이 일치한다.
- 생성기 테스트, C# 컴파일, FP1 회귀 테스트, FP2 scene 테스트가 통과한다.
- 0.5/1.0/1.5 m 거리 검증이 허용 범위에 있다.
- Preview와 Apply에서 빨간 큐브가 실제 태그와 일치한다.
- FP2 현장 fine tune이 두 수평 기준축으로 확정됐다.
- FP2-S1/S2/S3와 AAG/VG/NG에서 모든 콘텐츠 종류가 올바르게 이동한다.
- MRUK/TrackingSpace/Camera 계층이 보정으로 움직이지 않는다.
- 앱 완전 재실행 3회 이상에서 정렬이 재현된다.
- 모든 방의 물리 기준점 오차가 승인 범위 안이고 비강체 오차가 없다.
- JSONL에 적용값, root 수, 중단 원인이 재현 가능하게 기록된다.
- 읽기 전용 공간 변화 감시를 적용할 경우 자동 재정렬 없이 정확히 세션을 중단한다.

## 12. 관련 파일

- `Assets/Scenes/MainTest_FP1.unity`: 현재 현장 검증된 FP1 기준 씬
- `Assets/Scenes/MainTest_FP2.unity`: FP2 이식 대상 씬
- `Assets/Script/AagAprilTagTranslationAligner.cs`: 태그 검출·Preview·Apply
- `Assets/Script/AagRoom3TagReference.cs`: FP1 Room3 floor-local 기준
- `Assets/Script/AagFixedSpaceOffset.cs`: 앱 콘텐츠 평행이동
- `Assets/Script/ExperimentMain.cs`: 시작 gate, 콘텐츠 등록, JSONL 로그
- `Assets/Editor/AagRoom3AprilTagSetup.cs`: FP1 씬 저작 도구
- `Assets/Editor/AagRoom3AprilTagSceneSetupTests.cs`: FP1 하이브리드 회귀 테스트
- `Assets/Script/AagAprilTagRangeValidator.cs`: 1 m 거리 검증 HUD
- `Tools/FiducialTags/generate_hybrid_tags.py`: 현재 FP1 중심 태그 생성기
- `Assets/StreamingAssets/AAG/fp1_hybrid_tag_manifest.json`: FP1 runtime manifest
- `Documentation/ROOM3_APRILTAG_ALIGNMENT_SETUP_KO.md`: FP1 현장 설정 요약
- `Documentation/FP2_SPACE_SETUP.md`: FP2 공간 namespace/UUID 등록 절차

## 13. 롤백

1. 참가자 세션을 시작하지 않는다.
2. 실패한 FP2 APK를 제거하기보다 이전 승인 APK를 `adb install -r`로 덮어쓴다.
3. 앱 데이터를 지우거나 uninstall하면 저장 앵커와 persistent data가 사라질 수 있으므로
   명시적 백업 없이 수행하지 않는다.
4. Git에서는 FP1 안정 태그와 FP2 분리 체크포인트로 원인을 비교하되, 현재 작업 폴더의
   현장 export/백업을 삭제하거나 강제 reset하지 않는다.
5. 실패한 세션 JSONL과 당시 태그/공간 export를 보존하고 재현 후 수정한다.
