# Adaptive AI Guide — Meta Quest MR 공간 탐색 실험

Unity와 Meta Quest를 사용하여 여러 실제 방으로 구성된 혼합현실(MR) 공간에서 참가자의 탐색 행동을 측정하고, 안내 방식에 따른 차이를 비교하기 위한 실험 애플리케이션이다.

참가자는 공간에 배치된 색상별 돌을 찾아 인벤토리에 넣고, 같은 색상의 석탑에 전달한다. 시스템은 참가자의 이동, 방 방문, 머리 회전, 목표 발견과 전달 행동을 기록하며 실험 조건에 따라 적응형 음성 안내(AAG), 시각 안내(VG), 무안내(NG)를 제공한다.

현재 하나의 Unity 프로젝트에서 코드와 공통 에셋을 공유하면서, 실제 공간별 Room UUID·객체 UUID·설정·로그는 FP1과 FP2로 분리하는 구조를 사용한다.

## 프로젝트 목표

- 실제 다중 방 MR 공간에서 참가자의 탐색 및 목표 발견 행동을 재현 가능하게 측정한다.
- AAG, VG, NG 조건을 동일한 과업과 공간 배치 조건에서 비교한다.
- 참가자의 탐색 상태와 길 잃음 정도에 맞춰 AAG의 안내 강도를 조절한다.
- Meta Quest의 Space Setup 변경이나 앵커 위치 오차가 실험 데이터에 섞이지 않도록 세션 시작 전에 공간을 검증한다.
- FP1과 FP2의 공간 데이터가 서로 섞이지 않도록 UUID, 저장 파일, 로그 경로를 분리한다.
- 실험 배치, 실행 조건, 안내 결정과 참가자 행동을 로그로 남겨 사후 분석이 가능하도록 한다.

## 개발 환경

| 항목 | 현재 구성 |
|---|---|
| Unity | `6000.0.77f1` |
| 렌더 파이프라인 | Universal Render Pipeline `17.0.4` |
| Meta XR SDK | `203.0.1` |
| Meta OpenXR | `2.5.1` |
| 대상 기기 | Meta Quest / Android |
| Android 최소 SDK | API 32 |
| 애플리케이션 ID | `com.UnityTechnologies.com.unity.template.urpblank` |

FP1과 FP2는 현재 같은 애플리케이션 ID를 사용한다. 기존 Quest persistent data를 유지하면서 공간별 UUID를 등록하고 참가자 빌드를 업데이트하기 위한 선택이다. 두 버전을 한 기기에 동시에 설치해야 할 때만 application identifier 분리를 검토한다.

## 실험 조건과 참가자 과업

### 안내 조건

| 조건 | 동작 | 구현 목적 |
|---|---|---|
| AAG | 행동 지표를 평가해 안내 시점, 목적지 방, 안내 강도와 음성/자막 클립을 결정한다. | 참가자가 필요로 할 때만 개입하고 탐색 상태에 맞는 수준의 지원을 제공하기 위함이다. |
| VG | 헤드 방향을 기준으로 목표 위치를 투영한 원형 시각 가이드를 표시한다. | 지속적인 시각 안내 조건을 제공하여 적응형 음성 안내와 비교하기 위함이다. |
| NG | 목표 탐색을 위한 추가 안내를 제공하지 않는다. | 안내가 없는 기준 조건을 제공하기 위함이다. |

### 기본 과업 흐름

1. 실험자가 참가자 ID, 배치 세트(S1/S2/S3), 안내 조건(AAG/VG/NG)을 선택한다.
2. 시스템이 MRUK 공간, Room UUID, 시작 방, World Lock과 배치 데이터를 검증한다.
3. 선택한 세트의 돌 12개, 고정 석탑 4개, incidental object를 공간에 불러온다.
4. 참가자는 돌을 찾아 인벤토리에 저장한다. 인벤토리 용량은 3개다.
5. 참가자가 동일 색상의 석탑 전달 구역에 머무르면 3초 dwell 후 해당 색상의 돌이 전달된다.
6. 모든 목표가 전달되거나 제한 시간, 실험자 중단, 공간 무결성 오류가 발생하면 세션이 종료된다.

잘못된 색상의 석탑, 인벤토리 초과, 잘못된 grab, 전달 중 이탈과 같은 상태도 UI 피드백과 로그에 기록된다.

## 구현 기능과 구현 목적

### 1. FP1/FP2 실험 공간 분리

`ExperimentConfig`와 `ExperimentSpaceRuntime`이 활성 공간의 ID, floor plan, set ID, 저장 namespace와 로그 폴더를 결정한다.

| 분리 항목 | FP1 | FP2 |
|---|---|---|
| 메인 씬 | `Assets/Scenes/MainTest_FP1.unity` | `Assets/Scenes/MainTest_FP2.unity` |
| 설정 | `Assets/Experiment/FP1ExperimentConfig.asset` | `Assets/Experiment/FP2ExperimentConfig.asset` |
| Build Profile | `FP1_BuildProfile` | `FP2_BuildProfile` |
| Room UUID 스캐너 | `RoomUUIDScan.unity` | `RoomUUIDScan_FP2.unity` |
| 세션 로그 | `FP1Logs` | `FP2Logs` |
| 수동 앵커 manifest | `AagManualAnchorSets/fp1_manual_anchor_sets.json` | `AagManualAnchorSets/FP2/fp2_manual_anchor_sets.json` |
| room-local 배치 | `fp1_floor_anchor_local_placements.json` | `fp2_floor_anchor_local_placements.json` |
| PlayerPrefs UUID | 기존 `numUuids`, `uuidN` | `AAG.FP2.numUuids`, `AAG.FP2.uuid.N` |

구현 목적은 공통 코드와 프리팹은 재사용하되, 서로 다른 실제 공간에서 생성된 UUID와 배치 데이터가 교차 사용되는 것을 막는 것이다. FP2의 논리 세트는 `FP2-S1`~`FP2-S3`이고, 현재는 공통 모델을 재사용하기 위해 대응되는 FP1 Resources 프리팹 세트를 참조한다.

### 2. 공간 일치 검증과 fail-closed 세션 시작

`AagExperimentSpaceValidator`는 현재 MRUK가 불러온 Room UUID가 설정에 등록된 floor plan과 정확히 일치하는지 확인한다. 예상 방 누락, 알 수 없는 방, 중복 UUID, floor anchor 누락, World Lock 비활성, VR input focus 손실, 시작 방 불일치가 있으면 세션을 시작하지 않는다.

구현 목적은 잘못된 Space Setup이나 다른 물리 공간에서 실험이 실행되어 객체가 틀린 위치에 나타나거나 오염된 데이터가 수집되는 것을 방지하는 것이다. 현재 FP2 설정에는 8개 실제 Room UUID와 Room8 시작 방이 등록되어 있으며, 다른 UUID 조합이나 잘못된 시작 방에서는 검증에 실패한다.

### 3. 실험 세션 통합 관리

`ExperimentMain`이 참가자/세트/안내 조건 선택, 공간 준비, 객체 로딩, 세션 실행, 시간 제한, 완료 및 중단을 조정한다. 측정, 안내 판단과 파일 기록은 각각 `BehaviorMetrics`, `AAGGuide`, `LoggingManager`로 분리되어 있다.

구현 목적은 모든 조건이 동일한 세션 수명주기와 검증 절차를 사용하게 하고, 실험 도중 발생한 종료 원인을 명확히 기록하는 것이다. 실험자용 조작 창과 확인 절차는 참가자 UI와 분리되어 있으며, 추적 공간의 비정상적인 순간 이동도 공간 무결성 오류로 감지한다.

### 4. 공간 앵커 기반 객체 배치

공간에 저장된 Spatial Anchor UUID로 돌, 고정 석탑과 incidental object를 다시 불러온다.

- `PlacementSetManager`와 `AnchorLoader`: 선택한 S1/S2/S3 돌 배치 세트를 로드한다.
- `FixedTowerAnchorLoader`와 `FixedTowerManager`: 모든 세션이 공유하는 4개 석탑을 로드하고 세션별 색상 할당과 전달 수량을 관리한다.
- `IncidentalAnchorLoader`와 `IncidentalObjectManager`: 과업 외 환경 객체를 불러와 시각적 공간 구성을 유지한다.
- `AagManualAnchorSetAuthoring`: Quest에서 컨트롤러를 사용해 앵커를 생성·저장·삭제하고 세트 manifest를 작성한다.

구현 목적은 Unity 씬의 임의 world 좌표가 아니라 실제 공간에 저장된 기준점을 이용해 반복 실행에서도 동일한 물리 위치에 콘텐츠를 복원하는 것이다. 돌·석탑·incidental UUID의 중복도 세션 로딩 중 차단한다.

#### FP2의 현재 객체 배치 방식

FP2 참가자 빌드는 라이브 MRUK 메쉬의 위치를 객체 배치 기준으로 직접 사용하지 않는다. 번들에 저장된 baked MRUK 공간을 기준 좌표계로 사용하고, Room8에서 인식한 AprilTag의 yaw와 translation을 baked 공간 전체에 적용한다. 태그 pose의 앞뒤 방향이 모호할 때는 두 yaw 후보 중 현재 HMD가 baked Room8 안으로 들어오는 후보 하나만 채택한다. 둘 다 맞거나 둘 다 틀리면 정렬을 거절한다.

정렬 후 돌, 고정 석탑, incidental object에는 다음 안전 규칙이 FP2-S1/S2/S3 공통으로 적용된다.

1. 각 객체의 저장된 room-local 위치를 baked 공간으로 복원한다.
2. 방 경계 안이며 baked 벽에서 최소 `0.30 m` 떨어진 가장 가까운 위치를 선택한다.
3. Room7의 남자 화장실 끝과 현장에서 확인된 벽 포켓은 명시적 금지구역으로 제외한다.
4. 실제 FP2 참가자 추적 로그로 만든 방별 이동 경로에서 `0.45 m`보다 멀면 같은 방의 가장 가까운 유효 이동 경로 지점으로 옮긴다.
5. incidental object는 바닥에서 `0.20 m` 높이로 배치한다.

이 규칙은 특정 세트의 문제 객체만 예외 처리하는 방식이 아니다. 모든 세트의 모든 돌·석탑·incidental object가 같은 경로를 통과한다. 이미 참가자 동선 가까이에 있는 객체는 원래 위치를 유지하고, 엘리베이터·화장실·벽 뒤처럼 동선에서 벗어난 객체만 이동한다. Room7 파란 석탑의 안전 위치 `(2.65, 0.10)`도 같은 검증을 통과한다.

적용 결과는 `session.jsonl`과 Unity 로그의 `yawBranch`, `pathDistance`, `pathMove`, `wallMove` 값으로 확인할 수 있다. 배치 좌표를 수정할 때는 baked 방 경계, 명시적 금지구역, 참가자 동선 검증 테스트를 함께 갱신해야 한다.

### 5. room-local 배치와 rigid pose 복구

앵커 일부가 직접 localize되지 않거나 Meta가 공간 원점을 다시 설정한 경우, 저장된 room-local 좌표와 여러 검증 기준점으로 하나의 rigid transform을 계산해 배치를 복구한다. 복구 과정은 inlier/outlier와 RMS/최대 잔차를 검사하며 기준을 만족하지 못하면 임의 위치에 생성하지 않고 세션을 중단한다.

구현 목적은 장시간 이동이나 Space Setup 변경 뒤 발생할 수 있는 좌표계 이동에 대응하면서도, 근거가 부족한 추정 위치가 실험에 사용되는 것을 막는 것이다.

### 6. 방 좌표 및 MRUK 기하 내보내기

`AagRoomCoordinateExporter`는 Quest에서 다음 정보를 JSON으로 내보낸다.

- MRUK Room UUID와 연구자용 방 이름
- 방 anchor의 world pose와 방 중심
- floor, wall, doorway 등 MRUK anchor의 UUID, semantic label, plane/volume 정보
- local/world boundary vertex와 floor anchor 검증 경고

구현 목적은 실제 공간의 좌표와 방 구조를 PC에서 검토하고, 공간별 UUID 등록 및 배치 설계의 근거 데이터로 보존하는 것이다.

### 7. 결정론적 배치 저작과 검증

FP1 배치 저작 도구는 후보 위치를 생성하고 벽·문·장애물·객체 간 거리, 방별 quota, zone 중복, 발견 가능성, 시야 동시 노출과 세트 간 다양성을 검사한다. 승인된 hotspot이 부족하면 절차적 임의 위치로 대체하지 않고 정확한 실패 사유와 함께 생성을 중단한다.

구현 목적은 S1/S2/S3의 난이도와 분포를 통제하고, 매 실행마다 바뀌는 임의 배치가 실험 결과에 영향을 주지 않도록 하는 것이다. 현재 일부 배치 임곗값은 연구자 확정 전의 provisional 값이므로 현장 검토가 필요하다.

### 8. 적응형 안내(AAG)

`BehaviorMetrics`는 일정 시간 창에서 다음 신호를 측정한다.

- 이동 거리
- 방문한 고유 방 수
- 누적 머리 회전량
- 최근 목표 발견 수
- 빈손 상태의 재방문 시간 비율(RevisitProxy)

`AAGGuide`는 먼저 AES gate로 안내 개입이 필요한지 판단한다. 이후 RevisitProxy를 underload, optimal, overload 구간으로 분류하고 최근 4회 판단 중 3회가 같은 구간일 때 지원 수준을 조정한다. 지원 수준은 `VeryEasy`부터 `VeryHard`까지 5단계다.

목적지 방은 아직 방문하지 않은 방을 우선하고, 이후에는 의미 있는 방문이 가장 오래된 방과 남은 목표 수, 설정된 tie order를 사용해 선택한다. 참가자가 돌을 들고 있거나 모든 목표를 찾은 상태에서는 탐색 안내를 억제한다.

구현 목적은 단순한 주기적 안내가 아니라 실제 탐색 행동에 근거해 개입 여부와 정보량을 조절하고, 결정 근거를 로그로 재현할 수 있게 하는 것이다.

### 9. 시각 안내(VG)

VG 조건에서는 참가자의 머리 위치와 yaw를 기준으로 남은 목표의 방향과 거리를 원형 top-down 가이드에 투영한다. 목표를 인벤토리에 넣거나 전달하면 해당 마커 상태를 갱신하며 패널 표시 상태와 각 마커의 좌표도 로그로 남긴다.

구현 목적은 지속적으로 이용 가능한 시각적 방향 안내를 제공하고, AAG 및 NG 조건과 비교 가능한 독립 안내 조건을 만드는 것이다.

### 10. 음성·자막 안내 출력

`AAGUtterancePlayer`는 승인된 AAG 결정이 실제 출력으로 이어지는 단일 경로다. 개발 중에는 동일한 요청 경로로 자막을 표시하고, 참가자 실험에서는 설정된 AudioClip을 재생할 수 있다. 출력 시작, 정상 종료, 중단, 누락된 클립과 출력 비활성 사유를 기록한다.

구현 목적은 판단 로직과 출력 장치를 분리하고, 실제로 어떤 안내가 재생됐는지와 판단만 이루어졌는지를 구분하는 것이다.

### 11. 공간 정렬 방식

프로젝트는 다음 정렬 방식을 제공한다.

- MRUK/Spatial Anchor 기본 정렬
- 방별 fiducial(QR) marker 정렬
- 측정된 하나의 고정 X/Z translation 보정
- 다중 기준점을 이용한 yaw + translation 진단 및 rigid recovery

fiducial 정렬과 고정 translation은 동시에 활성화할 수 없다. 고정 translation은 모든 측정점이 하나의 평행이동으로 설명될 때만 사용한다.

구현 목적은 공간 오차의 형태를 진단한 뒤 그에 맞는 보정만 적용하고, 헤드셋이나 전체 tracking origin을 임의로 이동하지 않은 채 실험 콘텐츠만 정렬하는 것이다.

### 12. 행동 지표 및 구조화 로그

`LoggingManager`는 세션별 `session.jsonl` 파일의 단일 writer다. 설정 snapshot과 세션 시작/종료를 포함해 다음 이벤트를 기록한다.

- HMD 위치와 회전 track
- 방 입장·퇴장·체류·재방문
- AES와 RevisitProxy 값
- AAG 판단, 선택 방, 지원 수준, 클립 및 억제 사유
- 돌의 로드, grab, drop, 인벤토리 저장과 전달
- 석탑 전달 구역 진입·이탈·dwell 완료
- VG 패널과 마커 상태
- 앵커 로딩, 공간 보정, 복구와 검증 실패
- 앱 pause/quit, 시간 제한, 실험자 중단과 공간 무결성 중단

구현 목적은 참가자 행동뿐 아니라 당시의 설정, 시스템 판단과 공간 상태까지 함께 남겨 세션을 사후에 재구성하고 오류 세션을 판별할 수 있게 하는 것이다.

### 13. 현장 진단과 테스트

Editor 테스트는 공간 분리, rigid pose recovery, room-local 저장, 공간 offset solver, timing, 목표 발견 의미, fiducial 저장, 전달 UI, incidental prefab 가시성과 FP2 동선 제한을 검사한다.

구현 목적은 공간 데이터 누출과 좌표 복구 오류처럼 현장에서 발견하기 어려운 문제를 빌드 전에 확인하는 것이다. 2026-07-29 체크포인트에서는 런타임·Editor C# 컴파일과 `AagMrukSpaceCorrectionTests` 12개가 모두 통과했다.

## 주요 폴더와 파일

```text
Assets/
├─ Scenes/                    # FP1/FP2 참가자 씬과 UUID 스캐너 씬
├─ Experiment/                # 공간별 설정 및 AAG 오디오
├─ Script/                    # 런타임 실험, 공간, 안내, 로그 로직
├─ Editor/                    # Android 빌드 도구와 Editor 테스트
├─ Resources/                 # 런타임 로드 프리팹과 배치 세트
├─ StreamingAssets/           # 승인된 배치/좌표 카탈로그
└─ Settings/Build Profiles/   # FP1/FP2 Unity Build Profile
Documentation/                # FP2, fiducial, 공간 offset 운영 문서
QuestAnchorBackup/            # 현장 앵커/manifest 백업(일반적으로 Git 제외)
Exports/                      # Quest에서 가져온 로그·좌표 출력(일반적으로 Git 제외)
```

핵심 런타임 파일:

- `Assets/Script/ExperimentMain.cs`: 세션 전체 조정
- `Assets/Script/Fp1ExperimentConfig.cs`: 공간 중립 실험 설정 스키마와 기존 FP1 호환 타입
- `Assets/Script/ExperimentSpaceRuntime.cs`: 공간 namespace와 데이터 경로 선택
- `Assets/Script/AagExperimentSpaceValidator.cs`: MRUK 공간 검증
- `Assets/Script/BehaviorMetrics.cs`: 행동 지표 측정
- `Assets/Script/AAGGuide.cs`: 적응형 안내 결정
- `Assets/Script/AAGUtterancePlayer.cs`: 음성·자막 출력
- `Assets/Script/LoggingManager.cs`: JSONL 로그 기록
- `Assets/Script/AagManualAnchorSetAuthoring.cs`: Quest 수동 앵커 저작
- `Assets/Script/AagRoomCoordinateExporter.cs`: MRUK 좌표 내보내기

## 빌드 방법

### FP1 또는 FP2 참가자용 APK

가장 안전한 FP2 빌드 경로는 Unity 메뉴의 `AAG > Build FP2 Android APK`다. 이 메뉴는 FP2 씬과 설정을 명시적으로 선택해 APK를 만든다.

수동 Build Profile을 사용할 때는 다음을 확인한다.

1. Unity 버전이 `6000.0.77f1`인지 확인한다.
2. `File > Build Profiles`에서 FP2는 `FP2_BuildProfile`, FP1은 `FP1_BuildProfile`을 명시적으로 활성화한다.
3. FP2 프로필에는 `Assets/Scenes/MainTest_FP2.unity`만, FP1 프로필에는 `Assets/Scenes/MainTest_FP1.unity`만 참가자 메인 씬으로 포함됐는지 확인한다.
4. FP2 씬의 `ExperimentMain`이 `FP2ExperimentConfig.asset`을 참조하는지 확인한다.
5. 참가자 빌드에서는 manual authoring 기능이 꺼져 있는지 확인한다.
6. `Build` 또는 `Build And Run`을 실행한다.

`AAG > Build Android APK`는 기존 기본 빌드 경로이므로 FP2 현장 배포에는 `AAG > Build FP2 Android APK`를 사용한다. 이 저장소 체크포인트 작업에서는 APK 빌드와 기기 설치를 수행하지 않으며 최종 빌드·설치는 실험 운영자가 진행한다.

### Room UUID 스캐너 APK

- FP1: `AAG > Build FP1 Room UUID Scanner APK`
- FP2: `AAG > Build FP2 Room UUID Scanner APK`

스캐너는 참가자용 메인 씬이 아니라 공간 등록을 위한 전용 씬을 빌드한다.

## FP2 현장 배치 및 실행 절차

FP2 Room UUID, baked 공간, FP2-S1/S2/S3 room-local 배치와 오디오 매핑은 현재 등록되어 있다. 기존 Space Setup을 삭제하거나 새로 만들지 않는 한 객체를 다시 저작할 필요는 없다.

1. Quest에서 기존 FP2 Space Setup이 선택되어 있는지 확인한다.
2. `AAG > Build FP2 Android APK`로 만든 APK를 설치하고 앱을 실행한다.
3. 참가자 ID, FP2 세트, 안내 조건을 선택하되 아직 세션을 시작하지 않는다.
4. 실제 Room8 안에서 AprilTag 전체가 카메라에 보이도록 정면에 가깝게 바라보고 잠시 고정한다.
5. 화면에 tag 샘플, jitter와 yaw branch가 안정적으로 표시되는지 확인한다. 정상 후보는 `folded_expected_room` 또는 `opposite_expected_room`이다.
6. `APRILTAG: READY` 상태에서 세션을 시작한다. `REJECTED`, `NO ROOM8 MATCH`, `AMBIGUOUS BOTH MATCH`가 보이면 참가자를 진행시키지 말고 Room8 위치와 태그 가시성을 다시 확인한다.
7. 세션 시작 후 돌·석탑·incidental object가 벽·엘리베이터·화장실 안이 아니라 참가자 동선 쪽에 나타나는지 첫 실행에서 확인한다.
8. 실행 종료 후 `FP2Logs/<session-id>` 전체와 `spawned_incidental_positions.csv`, `spawned_object_answer_key.csv`, `spawned_stone_positions.csv`를 참가자별 원본 폴더로 백업한다.

자세한 절차는 [`Documentation/FP2_SPACE_SETUP.md`](Documentation/FP2_SPACE_SETUP.md)를 참조한다.

## 로그 위치

Quest의 `Application.persistentDataPath` 아래에 공간별로 저장된다.

```text
FP1Logs/<session-id>/session.jsonl
FP2Logs/<session-id>/session.jsonl
```

세션 ID에는 참가자, 공간/세트, 안내 조건과 실행 시각을 구분할 수 있는 정보가 포함된다. 현장 수집 후 원본 폴더를 그대로 복사하고, 분석용 변환본과 분리해 보관하는 것을 권장한다.

## 검증

C# 컴파일 확인:

```powershell
dotnet restore Assembly-CSharp.csproj -v:minimal
dotnet restore Assembly-CSharp-Editor.csproj -v:minimal
dotnet build Assembly-CSharp.csproj --no-restore -v:minimal
dotnet build Assembly-CSharp-Editor.csproj --no-restore -v:minimal
```

Unity Editor에서는 `Window > General > Test Runner`에서 EditMode 테스트를 실행한다. FP2 참가자 빌드 전에는 최소한 다음을 확인한다.

- FP2 씬이 FP2 config를 참조하는가
- FP2 scanner가 FP2 namespace를 사용하는가
- FP2 Build Profile에 FP2 메인 씬만 포함되는가
- FP2 설정과 파일에 FP1 Room UUID가 들어 있지 않은가
- 실제 FP2 Room UUID와 시작 방 검증이 통과하는가
- manual authoring 기능이 참가자 빌드에서 꺼져 있는가
- `AagMrukSpaceCorrectionTests`가 모두 통과하는가. 이 테스트는 8개 방의 동선 후보가 baked 경계와 금지구역을 통과하는지, 현장 문제 객체가 동선으로 이동하는지 검사한다.

## 안전 체크포인트와 Git 운용

- FP1 안정 체크포인트: `ab1fcaf`
- FP1 복구 태그: `fp1-stable-20260724`
- FP2 분리 체크포인트: `9be83cb`
- FP2 공간 안전 체크포인트: `fp2-spatial-safety-checkpoint-20260728`
- FP2 현재 작업 브랜치: `codex/fp2-hybrid-tag-runbook`

소스 코드, Unity 씬, `.meta`, 설정 에셋과 운영 문서는 Git으로 관리한다. APK, Quest 원본 로그, 대용량 export, 임시 진단 파일, 개인 백업은 명시적으로 검토하지 않은 상태에서 커밋하지 않는다.

## 현재 주의사항

- FP2는 공통 프리팹 에셋을 재사용하지만 UUID manifest와 persistent data는 FP1과 공유하지 않는다.
- FP1/FP2 application identifier가 동일하므로 동시에 설치할 수 없다. 서로 다른 ID로 바꾸면 persistent data도 분리된다.
- AprilTag 정렬은 Room8에서 수행해야 하며 HMD가 baked Room8로 검증되지 않는 yaw 후보는 적용하지 않는다.
- 참가자 동선 제한은 실제 추적 로그에서 만든 안전 후보를 사용한다. baked 공간이나 금지구역을 변경하면 동선 테스트도 다시 실행해야 한다.
- 배치 저작 도구의 일부 연구 임곗값은 provisional 상태다. 현장 검토 없이 최종 실험 기준으로 간주하지 않는다.
- Space Setup을 삭제하거나 다시 만들면 Room/Anchor UUID가 바뀔 수 있다. 기존 데이터를 무조건 재사용하지 말고 스캔과 검증을 다시 수행한다.
- `Library`, `Temp`, `Logs`, APK, Quest 백업과 export는 프로젝트 복구용 자료일 수 있으나 일반 소스 커밋과 분리한다.

## 세부 문서

- [`Documentation/FP2_SPACE_SETUP.md`](Documentation/FP2_SPACE_SETUP.md): FP2 공간 등록 및 빌드
- [`Documentation/FIDUCIAL_MARKER_PILOT.md`](Documentation/FIDUCIAL_MARKER_PILOT.md): 방별 fiducial 정렬
- [`Documentation/SPACE_OFFSET_DIAGNOSTIC_KO.md`](Documentation/SPACE_OFFSET_DIAGNOSTIC_KO.md): 공간 오프셋 진단과 고정 보정
- [`README_AAG_MANUAL_ANCHOR_SET_AUTHORING.md`](README_AAG_MANUAL_ANCHOR_SET_AUTHORING.md): 수동 앵커 세트와 핵심 객체 로더
- [`README_AAG_ROOM_COORDINATE_EXPORT.md`](README_AAG_ROOM_COORDINATE_EXPORT.md): MRUK 방 좌표 export와 배치 저작
- [`README_AAG_HOTSPOT_PERSISTENCE.md`](README_AAG_HOTSPOT_PERSISTENCE.md): 승인 hotspot 복구와 저장 수명주기
