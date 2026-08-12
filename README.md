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

## FP1·FP2 현장 오류 이력과 최종 대응 (2026-08-12 체크포인트)

이 절은 FP1과 FP2를 실제 Quest 공간에서 반복 점검하면서 확인한 오류, 원인, 대응을 기록한다. 가장 중요한 안전 기준은 **돌·돌탑·우연 객체가 벽이나 기둥에 박히거나 창문 밖으로 나가지 않는 것**이다. 단순히 좌표가 로드되거나 태그가 인식되는 것만으로 배치 성공으로 간주하지 않는다.

### 오류와 대응 요약

| 구분 | 실제 증상 | 원인 | 최종 대응 |
|---|---|---|---|
| FP1/FP2 데이터 혼용 | 다른 공간의 Room UUID와 배치가 로드되거나 검증이 잘못 통과함 | 공통 코드에서 공간별 manifest, PlayerPrefs, 로그 경로가 충분히 분리되지 않음 | `ExperimentSpaceRuntime`을 기준으로 FP1/FP2 설정, UUID namespace, 배치 리소스, 씬, 스캐너와 로그 폴더를 분리함 |
| AprilTag 미인식 | 태그를 바라봐도 시작 준비 상태가 되지 않음 | 태그가 있는 실제 방과 선택한 스캔 데이터가 다르거나, 새 스캔만 선택하면서 기준 태그 방을 잃음 | FP1은 기존 baked 공간의 Room3(새 스캔 이름 `이름없는 룸 6`) 태그 기준을 유지하고, FP2와 동일하게 태그를 응시한 뒤 컨트롤러 입력으로 확정함 |
| 태그 정렬 180도 반전 | 태그는 잡히지만 전체 객체가 반대편 공간으로 이동함 | 평면 태그 pose의 yaw 후보가 앞/뒤 방향에서 모호함 | 후보 yaw를 모두 계산하고 HMD가 baked 기준 기대 방 안에 들어가는 후보만 선택함. `folded_expected_room` 또는 `opposite_expected_room` 진단을 로그에 기록함 |
| 태그 순간 흔들림 | 인식 직후 공간이 조금씩 흔들리거나 잘못된 정렬이 적용됨 | 단일 프레임 pose와 불안정한 yaw 샘플을 즉시 사용함 | 다중 샘플의 위치/yaw jitter를 검사하고 안정된 상태에서만 rigid correction을 적용함. 유효하지 않으면 fail-closed로 세션 시작을 막음 |
| live MRUK와 저장 배치 불일치 | Room UUID는 맞아도 객체가 전체적으로 밀려남 | 재스캔 또는 Space Setup 변경으로 MRUK 원점·방 pose가 달라짐 | 저장 당시 baked MRUK 공간을 배치 기준으로 사용하고 AprilTag의 yaw+translation을 공간 전체에 하나의 rigid transform으로 적용함 |
| 새 스캔만 사용한 FP1 | FP1 태그를 아예 찾지 못하거나 기존 배치 기준이 사라짐 | 최신 스캔을 기존 baked 데이터의 대체재로 사용함 | FP1에서는 baked 공간을 태그·룸·배치의 권위 데이터로 유지하고 새 스캔은 변경된 벽/기둥/창문을 거부하는 보조 wall map으로만 사용함 |
| 오래된 baked 데이터만 사용한 FP1 | 평면도가 조금 달라진 구간에서 돌이 벽·기둥 안 또는 창밖에 생성됨 | 실제 공간의 국소 구조 변화가 baked 경계에 반영되지 않음 | 최종 룸 매핑을 이용해 baked floor-local 좌표를 새 스캔으로 변환하고, 새 경계와 벽에서 `0.45 m` 이내인 점을 추가로 거부함 |
| Room2 분할 누락 | Room2 주변 경계 보정과 룸 이름 대응이 잘못됨 | 최종 스캔에서 Room2가 Room2-1과 Room2-2로 나뉜 사실이 초기 매핑에서 빠짐 | 아래의 9개 논리 공간 매핑으로 확정하고 supplemental wall map을 다시 생성함 |
| 벽에서 몇 cm만 이동 | 안전 보정 로그는 남지만 돌이 여전히 벽이나 창밖에 보임 | 가장 가까운 안전점을 찾는 국소 이동만으로는 스캔 오차와 객체 크기를 흡수하지 못함 | 현장에서 확인된 문제 객체 6개는 두 스캔 모두에서 벽 여유가 가장 큰 검증 waypoint, 즉 해당 룸 중앙 안전점으로 명시적으로 이동함 |
| 우연 객체가 잘못된 룸으로 보정 | Room2-2/Hall1-2 경계의 항아리가 다른 복도 기준으로 이동함 | 월드 위치에서 가장 가까운 동선만 선택해 원래 소속 룸을 추정함 | 문제 우연 객체는 객체 ID별 소속 룸을 명시하고 그 룸의 중앙 안전점으로 이동함 |
| 돌탑이 누움 | 돌탑 전체가 바닥에 눕거나 벽 안에 생성됨 | floor anchor 회전을 이미 world-upright인 돌탑 회전에 다시 곱해 90도 pitch가 추가됨 | FP1 돌탑 회전은 world-yaw/upright 기준을 보존하고 태그의 rigid correction만 적용함. 위치에는 벽·동선 검사를 적용함 |
| 창문·외부 공간 생성 | 특히 Room7/Room8 노란 돌이 실제 창문 밖에 생성됨 | floor polygon만 통과하고 창문/벽 선분과 충분한 여유를 확보하지 못함 | baked 경계, baked wall, supplemental rescan 경계와 wall segment를 모두 검사하고, 재현된 객체는 중앙 override를 사용함 |
| 빌드 메뉴 혼동 | 일반 Android, FP1, FP2, scanner APK 중 잘못된 메뉴를 선택할 위험 | 여러 목적의 빌드 메뉴가 한곳에 존재함 | 참가자 FP1은 `AAG > Build FP1 Android APK`, FP2는 `AAG > Build FP2 Android APK`만 사용함. UUID scanner와 validator는 현장 등록/진단 전용임 |
| Unity EditMode 실행 실패 | 배치 테스트가 시작되지 않거나 프로젝트 잠금 오류 발생 | Unity Hub 라이선스 미로그인 또는 같은 프로젝트를 에디터와 batchmode가 동시에 열음 | Hub 로그인 후 원본 프로젝트의 에디터를 닫고 batchmode 테스트를 실행함. 복사본 결과를 최종 근거로 사용하지 않음 |
| `dotnet --no-restore` 실패 | `project.assets.json`이 없다는 오류로 컴파일 검사가 중단됨 | Unity가 `Temp/obj`를 정리해 임시 NuGet 자산이 사라짐 | `dotnet restore` 후 런타임과 Editor 어셈블리를 다시 빌드함 |

### FP1 최종 룸 매핑

왼쪽은 기존 baked 논리 공간이고 오른쪽은 2026-08-12 보조 스캔에서 표시된 이름이다.

| 기존 논리 공간 | 새 스캔 이름 |
|---|---|
| Room1 | 이름없는 룸 |
| Room2-1 | 이름없는 룸 3 |
| Room2-2 | 이름없는 룸 4 |
| Room3 | 이름없는 룸 6 |
| Hall 1-1 | 이름없는 룸 2 |
| Hall 1-2 | 이름없는 룸 5 |
| Hall 1-3 | 거실 |
| Hall 2-1 | 이름없는 룸 7 |
| Hall 2-2 | 이름없는 룸 8 |

### FP1 현장 재현 사례와 조치

- 모든 세션에서 Room1과 Hall 1-1 사이 벽에 돌탑이 박힘: baked 위치를 유지하되 supplemental wall veto와 FP1 walkable path를 적용하고 upright 회전을 보존함.
- S1 `yellow_3`가 이름없는 룸 7의 창밖에 생성됨: Hall 2-1 중앙 안전점으로 이동함.
- S1 `yellow_1`이 이름없는 룸 3의 기둥 벽 안에 생성됨: Room2-1 중앙 안전점으로 이동함.
- S2 `yellow_1`이 이름없는 룸 7의 창밖에 생성됨: Hall 2-1 중앙 안전점으로 이동함.
- S2 `incidental_02_02_jar`가 이름없는 룸 4/5 경계에서 Room2-2 벽에 박힘: Room2-2 중앙 안전점으로 이동함.
- S3 `yellow_2`가 이름없는 룸 8과 이름없는 룸 7 경계의 벽에 박힘: Hall 2-2 중앙 안전점으로 이동함.
- S3 `incidental_02_02_hairDryer`가 이름없는 룸의 문 쪽에 박힘: 카탈로그상 소속인 Hall 1-1(이름없는 룸 2) 중앙 안전점으로 이동함.

이 6개 override는 `(setId, objectId)` 또는 incidental `objectId`에만 적용한다. 다른 객체는 안전한 원래 위치를 유지하고, 일반 벽/동선 검사에서 실패한 경우에만 이동한다. 높이와 회전은 유지하고 floor-local 수평 위치만 중앙점으로 바꾼다.

### 현재 최종 구조

#### FP1

1. 기존 `fp1_baked_mruk_scene.json`이 태그 정렬, 방/floor pose와 저장 배치의 기준이다.
2. `fp1_supplemental_wall_map.json`은 새 스캔에서 바뀐 경계·벽·기둥·창문을 확인하는 보조 veto다.
3. `AagFp1WalkablePath`는 baked와 supplemental 양쪽을 통과한 현장 이동 waypoint만 사용한다.
4. 일반 객체는 안전하면 원래 위치를 유지하고, 안전하지 않으면 가까운 검증점으로 이동한다.
5. 위 6개 현장 재현 객체는 가장 큰 복합 벽 여유를 가진 룸 중앙 검증점으로 이동한다.
6. supplemental 매핑이 없거나 손상되면 임의 fallback 없이 배치를 중단한다.

#### FP2

1. FP2 전용 baked MRUK, Room UUID, set manifest, room-local 배치와 로그 namespace를 사용한다.
2. Room8 AprilTag를 응시해 얻은 yaw+translation으로 baked 공간 전체를 정렬한다.
3. 객체는 baked 방 경계와 wall clearance, 명시적 금지구역, 참가자 추적 기반 walkable path를 통과해야 한다.
4. 안전한 저작 위치는 유지하고 위험한 위치만 검증 경로로 옮긴다.
5. FP2 정답표 SVG와 스폰 CSV는 실제 세션의 `target_loaded`, `target_room_local_loaded`, `incidental_loaded` 로그를 기준으로 생성한다.

### 검증 기록과 알려진 테스트 상태

- 2026-08-12 런타임 `Assembly-CSharp`와 Editor `Assembly-CSharp-Editor` C# 컴파일: 오류 0.
- FP1 supplemental/walkable EditMode 테스트: 10/10 통과 기록.
- 당시 전체 EditMode 테스트: 82개 중 80개 통과. 남은 2개는 이번 벽/태그 배치 오류와 직접 관련 없는 기존 기대값 문제였다.
  - best-effort rigid recovery의 RMS 문자열 기대값 `0.1689`와 계산값 `0.1523` 불일치.
  - FP2 공간 분리 테스트가 현재 등록된 8개 FP2 방 대신 빈 목록을 기대함.
- 최종 APK 빌드와 Quest 설치·현장 확인은 Unity 에디터에서 운영자가 수행한다.

### 체크포인트 운용 원칙

- 이 체크포인트는 FP1과 FP2의 현재 현장 운용 기준을 함께 보존한다.
- APK, 참가자 원본 로그, 개인 백업, `Library`, `Temp`, 로컬 Unity AI 설정과 공간 메모리 캐시는 Git 체크포인트에 포함하지 않는다.
- Room Setup을 다시 생성하거나 태그 위치, 벽 구조, 창문/기둥 형상이 바뀌면 룸 export와 supplemental map, walkable path 테스트를 다시 만들어야 한다.
- 새 현장 오류를 발견하면 전역 좌표를 임의 수정하지 말고 `(공간, 세트, 객체, 소속 룸, 재현 사진/로그)` 단위로 기록한 뒤 최소 범위 override 또는 안전 규칙으로 대응한다.

## AAG 음성 가이드 메커니즘과 단계별 발화

이 절의 AAG는 `Adaptive AI Guide` 조건을 뜻한다. 참가자 실행에서는 실시간 TTS를 합성하지 않고 `ExperimentConfig`에 미리 연결된 `AudioClip`을 2D(non-spatial) 음성으로 재생한다. 아래 문장은 각 클립에 함께 등록된 발화문(`captionText`)이다. 개발용 text mode를 켜면 동일한 결정 경로에서 음성 대신 머리 고정 자막으로 3초간 표시되지만, 현재 FP1/FP2 참가자 설정은 모두 `text mode = off`, `audio playback = on`이다.

### 공통 작동 흐름

| 순서 | 처리 | 실제 동작 |
|---|---|---|
| 1. 행동 측정 | HMD 위치·yaw·pitch와 현재 방을 `0.1초`마다 표본화 | 이동거리, 누적 머리 회전, 방문 방, 최근 돌 발견, 빈손 시간과 재방문 시간을 계산한다. 방 변경은 같은 방 UUID가 `0.5초` 유지되어야 확정한다. |
| 2. AAG 판단 | `2초`마다 `BehaviorMetrics.Evaluate()`와 `AAGGuide.Evaluate()` 실행 | 최근 AES 창에서 탐색 활동을 판정하고, 현재 설정에 따라 지원 단계 또는 정체 단계와 추천 방을 고른다. |
| 3. AES loose gate | 네 탐색 증거를 함께 검사 | 이동거리·고유 방문 방·머리 회전이 **모두** 기준 미만이고 최근 발견도 0일 때만 gate failure다. 네 증거 중 하나라도 충족하면 gate pass다. |
| 4. 추천 방 선택 | 아직 돌이 남은 방만 후보로 구성 | 현재 방을 제외하고 `미방문 방 → 가장 오래 전에 의미 있게 방문한 방` 순으로 고른다. 미방문 방끼리는 남은 돌 수와 `tieOrder`를 사용한다. |
| 5. 발화 억제 | 말하면 안 되는 상태를 먼저 제거 | 모든 돌을 찾음, 돌을 들고 있음, cold start, 최소 발화 간격 미충족, 출력 비활성, 오디오 재생 중, 클립 누락이면 말하지 않는다. 억제 판단도 로그에 남긴다. |
| 6. 음성 출력 | 승인된 clip ID를 `AAGUtterancePlayer`가 재생 | 한 번에 한 클립만 재생한다. `GF/N/H/VH` 계열이 직전 발화와 같으면 `01 ↔ 02`로 교대한다. 룸별 `VE/E`는 방문 상태와 선택된 방에 따라 결정된다. |
| 7. 기록 | 결정과 실제 출력 수명주기를 분리해 저장 | 매 판단은 `decision`, 재생 완료는 `clip_finished`, 중단은 `clip_interrupted`로 기록한다. `wouldPlayClipId`, 실제 `playedClipId`, 억제 사유도 보존한다. |

### FP1과 FP2의 발화 규칙이 다른 이유

**직접적인 변경 근거는 FP2 파일럿에서 관찰된 기존 재방문 proxy의 한계**이며, **FP1의 복도식 구조와 FP2의 분산식 구조는 그 결과를 설명하는 공간적 해석**이다. 공간 구조만의 독립 효과를 검증한 것은 아니므로 두 근거를 동일하게 취급하지 않는다.

| 구분 | FP1 | FP2 | 설계상 의미 |
|---|---|---|---|
| 공간 구조에 대한 해석 | 길게 연결된 복도와 순차적인 공간 전이가 중심 | 여러 방에 목표물이 분산되고 방 내부의 국소 탐색 비중이 큼 | FP1에서는 이동·방 전이·재방문이 탐색 상태를 잘 드러내지만, FP2에서는 이동량이 작아도 한 방에서 오래 헤맬 수 있음 |
| 주 판단 신호 | 이동·방 방문과 빈손 재방문 비율 | 마지막 돌 발견 이후 연속 빈손 시간 | FP2는 “얼마나 돌아다녔는가”보다 “새 목표를 못 찾은 상태가 얼마나 지속됐는가”를 우선 사용 |
| 개입 방식 | 재방문 proxy에 따라 `VeryEasy..VeryHard` 5단계 조절 | `<25초` 무음, `25–<70초` 룸 힌트, `≥70초` terminal fallback 허용 | 복도식 탐색에는 점진적 난이도 조절, 분산식 마지막 목표 탐색에는 정체시간 기반 개입을 적용 |
| 기존 동작 보존 | 기존 FP1 규칙과 테스트를 그대로 유지 | 새 정체 모델을 설정값으로 opt-in | FP2 개선이 이미 안정화된 FP1 실행을 소급 변경하지 않도록 분리 |

FP2 변경의 직접적인 파일럿 근거는 다음과 같다.

- 기존 proxy는 돌을 찾을 때마다 초기화되어 파일럿 세션의 약 `17–48%`가 `cold_start`에 머물렀고, 방향 상실을 즉시 표현하지 못했다.
- FP2의 방향 상실은 일반 탐색 전반보다 마지막 돌을 찾는 구간에 집중되었다.
- P01–P07의 돌 발견 간격에서 일반 탐색은 약 `p75=20초`, `p95=65초`였고, 마지막 돌 정체 구간은 약 `73–333초`였다.
- 이 분포를 바탕으로 FP2의 간접 힌트 기준을 `25초`, 상위 정체 기준을 `70초`로 정하고 최소 발화 간격도 `25초`로 늘렸다.
- 이 임곗값은 파일럿 기반 provisional 값이며 별도 validation session에서 재확인해야 한다. 변경 근거는 Git commit [`adf41a5`](https://github.com/rlaqkqgog/adaptive-ai-guide/commit/adf41a5b3be75daf703437a2d61c9f6d5c9c7812)에도 기록되어 있다.

따라서 현재 구현은 “동일한 AAG를 두 공간에 그대로 적용한 조건”이 아니라 **각 평면도에서 관찰된 탐색 양상에 맞춰 보정된 두 AAG 정책**이다. 분석에서 FP1과 FP2 성과를 직접 비교할 경우 공간 구조와 AAG 정책 차이가 함께 작용하는 혼입 가능성을 명시하고, 최소한 `floorPlanId × AAG policy version`으로 구분해 분석해야 한다. 공간 효과만을 인과적으로 비교하려면 두 평면도에 동일한 발화 규칙을 적용한 별도 통제 실행이 필요하다.

또한 FP2의 현재 룸 선택은 참가자가 서 있는 현재 방을 후보에서 제외한다. 따라서 정체시간 모델은 “다른 방을 다시 확인할 시점”은 잘 포착하지만, 현재 방 안의 돌을 놓친 경우 “이 방을 더 꼼꼼히 보세요”라고 직접 안내하지는 못한다. 이는 분산형 공간의 국소 탐색을 완전히 모델링하지 못하는 현재 구현의 제한점이다.

### FP1: 평면도 1 AAG

#### FP1 판단 파라미터

| 항목 | FP1 설정과 의미 |
|---|---|
| 시작 공간 | `room3`(419강의실) |
| AES 창·gate | 최근 `24초`; 이동 `< 12 m`, 방문 방 `< 2`, 머리 회전 `< 500°`, 최근 발견 `0`이 모두 성립할 때만 GF 발화 후보 |
| 재방문 proxy | 최근 `48초` 빈손 구간 중 재방문 구간 비율. 누적 빈손 `24초` 전에는 cold start |
| 적응 구간 | `< 0.25 = underload`, `0.25–<0.71 = optimal`, `≥ 0.71 = overload` |
| 지원 단계 변경 | 최근 유효 zone 4개 중 3개 이상이 underload면 한 단계 **어렵게**, overload면 한 단계 **쉽게**, optimal이면 유지. `VeryEasy..VeryHard` 범위로 제한 |
| 초기 지원 단계 | `Normal` |
| 최소 발화 간격 | `15초` |
| 방문 방 재추천 | 방을 나온 뒤 최소 `60초`; 체류 `5초` 이상인 방문만 의미 있는 방문 시각 갱신 |
| 정체시간 override | 사용 안 함(`stagnation thresholds = 0`)—FP1은 5단계 적응 모델이 실제 clip 선택을 담당 |

#### FP1 단계별 발화 선택

| 단계/조건 | 선택되는 발화 |
|---|---|
| 세션 시작 후 AES 증거가 아직 `24초` 미만 | 무음(`aes_cold_start`) |
| AES gate failure | `GF-01` “좀 더 둘러보시겠어요?” / 반복 시 `GF-02` “다른 곳도 자유롭게 다녀보세요.” |
| `VeryEasy`, 선택 방 미방문 | 룸별 `VE-U-*`: “아직 안 가보신 …을 확인해보세요.” |
| `VeryEasy`, 선택 방 방문함 | 룸별 `VE-V-*`: “…은/는 꼼꼼히 살펴보셨나요?” |
| `Easy` | 룸별 `E-*`: “… 근처도 한번 살펴보세요.” |
| `Normal` | `N-01` “아직 찾지 못한 돌이 남아 있어요.” / 반복 시 `N-02` “남은 돌이 아직 있어요.” |
| `Hard`, 선택 방 미방문 | `H-01` “아직 둘러보지 않은 공간이 남아 있어요.” / 반복 시 `H-02` “가보지 않은 곳이 아직 있어요.” |
| `Hard`, 추천할 미방문 방 없음 | `Normal`로 내려가 `N-01/02` 사용 |
| `VeryHard` | `VH-01` “계속 자유롭게 다녀보세요.” / 반복 시 `VH-02` “편하신 대로 계속 진행하세요.” |
| 돌을 들고 있음 / 찾을 돌이 없음 | 무음(`carrying` / `no_remaining_targets`) |

#### FP1 룸별 발화표

Room2가 최종적으로 Room2-1과 Room2-2로 분리되었기 때문에 두 공간은 서로 다른 룸 후보로 판단하지만 같은 스튜던트 라운지 음성 파일을 공유한다. 에셋에 남은 구형 `room2` clip ID는 호환용이며 최종 9룸 매핑에서는 직접 선택되지 않는다.

| 논리 공간 | 현장 명칭 | 미방문 `VE-U` | 방문 `VE-V` | `Easy` |
|---|---|---|---|---|
| Room1 (`room1`) | 원우회실 | 아직 안 가보신 원우회실을 확인해보세요. | 원우회실은 꼼꼼히 살펴보셨나요? | 원우회실 근처도 한번 살펴보세요. |
| Room2-1 (`room2-1`) | 스튜던트 라운지 | 아직 안 가보신 스튜던트 라운지를 확인해보세요. | 스튜던트 라운지는 꼼꼼히 살펴보셨나요? | 스튜던트 라운지 근처도 한번 살펴보세요. |
| Room2-2 (`room2-2`) | 스튜던트 라운지 2 | 아직 안 가보신 스튜던트 라운지를 확인해보세요. | 스튜던트 라운지는 꼼꼼히 살펴보셨나요? | 스튜던트 라운지 근처도 한번 살펴보세요. |
| Room3 (`room3`) | 419강의실 | 아직 안 가보신 419강의실을 확인해보세요. | 419강의실은 꼼꼼히 살펴보셨나요? | 419강의실 근처도 한번 살펴보세요. |
| Hall 1-1 (`hall1-1`) | 베란다 쪽 복도 | 아직 안 가보신 베란다 쪽 복도를 확인해보세요. | 베란다 쪽 복도는 꼼꼼히 살펴보셨나요? | 베란다 쪽 복도도 한번 살펴보세요. |
| Hall 1-2 (`hall1-2`) | 라운지 앞 복도 | 아직 안 가보신 라운지 앞 복도를 확인해보세요. | 라운지 앞 복도는 꼼꼼히 살펴보셨나요? | 라운지 앞 복도도 한번 살펴보세요. |
| Hall 1-3 (`hall1-3`) | 정수기 근처 복도 | 아직 안 가보신 정수기 근처 복도를 확인해보세요. | 정수기 근처 복도는 꼼꼼히 살펴보셨나요? | 정수기 근처도 한번 살펴보세요. |
| Hall 2-1 (`hall2-1`) | 화장실 앞쪽 | 아직 안 가보신 화장실 앞쪽을 확인해보세요. | 화장실 앞쪽은 꼼꼼히 살펴보셨나요? | 화장실 앞쪽도 한번 살펴보세요. |
| Hall 2-2 (`hall2-2`) | 계단 쪽 | 아직 안 가보신 계단 쪽을 확인해보세요. | 계단 쪽은 꼼꼼히 살펴보셨나요? | 계단 쪽도 한번 살펴보세요. |

### FP2: 평면도 2 AAG

#### FP2 판단 파라미터

| 항목 | FP2 설정과 의미 |
|---|---|
| 시작 공간 | `room8`(413강의실) |
| AES 창·gate | 최근 `16초`; 이동 `< 6 m`, 방문 방 `< 2`, 머리 회전 `< 360°`, 최근 발견 `0`이 모두 성립할 때만 gate failure |
| 재방문 proxy | 최근 `24초`, low/high 경계 `0.20/0.55`; 현재는 계산·로그만 하고 clip 단계는 바꾸지 않음 |
| 실제 발화 단계 기준 | 돌을 들지 않은 상태에서 마지막 발견 이후 연속 빈손 시간: `< 25초`, `25–<70초`, `≥ 70초` |
| 최소 발화 간격 | `25초` |
| 방문 방 재추천 | 방을 나온 뒤 최소 `30초`; 체류 `3초` 이상인 방문만 의미 있는 방문 시각 갱신 |
| 초기 지원 단계 | `VeryEasy`; 정체 모델이 활성화되어 실제 룸 힌트도 `VeryEasy` 형식으로 강제 |

#### FP2 단계별 실제 발화 선택

| 빈손 정체 단계 | AES 상태 | 작동과 발화 |
|---|---|---|
| `< 25초` (`encourage`) | pass/failure 공통 | 정상 탐색 속도로 보고 무음(`stagnation_low`) |
| `25–<70초` (`indirect`) | gate failure | `GF-01` “좀 더 둘러보시겠어요?”; 직전과 같으면 `GF-02` “다른 곳도 자유롭게 다녀보세요.” |
| `25–<70초` (`indirect`) | gate pass | 남은 돌이 있는 비현재 방을 골라 미방문이면 `VE-U-*`, 방문했으면 `VE-V-*` 발화 |
| `≥ 70초` (`top`) | gate failure | `GF-01/02` 교대 |
| `≥ 70초` (`top`) | gate pass | 같은 `VE-U/VE-V` 룸 힌트. 남은 비현재 후보가 하나뿐이면 30초 재추천 cooldown 중이어도 가장 오래된 방문 방을 강제로 다시 안내 가능 |
| 돌을 집어 들고 이동 | 관계없음 | 빈손 시간이 0으로 돌아가며 AAG 무음(`carrying`) |
| 모든 돌을 찾음 | 관계없음 | 검색 안내 자체를 중단(`no_remaining_targets`) |

FP2 설정 에셋에도 `E/N/H/VH` 음성이 등록되어 있으나, 현재 정체시간 override가 clip 선택을 덮어쓰기 때문에 정상 실행의 주 경로는 `무음 → GF 또는 VE 룸 힌트`다. 추천 방을 만들 수 없는 예외적인 gate-pass 상황에서만 `N-01/02` fallback이 가능하다.

#### FP2 룸별 발화표

| 논리 공간 | AAG 현장 명칭 | 미방문 `VE-U` | 방문 `VE-V` | 등록된 `Easy` 음성(현재 주 경로에서는 미사용) |
|---|---|---|---|---|
| Room1 (`room1`) | 414 강의실 | 아직 안 가보신 414 강의실을 확인해보세요. | 414 강의실은 꼼꼼히 살펴보셨나요? | 414 강의실 근처도 한번 살펴보세요. |
| Room2 (`room2`) | 412 강의실 | 아직 안 가보신 412 강의실을 확인해보세요. | 412 강의실은 꼼꼼히 살펴보셨나요? | 412 강의실 근처도 한번 살펴보세요. |
| Room3 (`room3`) | 412 강의실 앞쪽 | 아직 안 가보신 412 강의실 앞쪽을 확인해보세요. | 412 강의실 앞쪽은 꼼꼼히 살펴보셨나요? | 412 강의실 앞쪽도 한번 살펴보세요. |
| Room4 (`room4`) | 메인 홀 | 아직 안 가보신 메인 홀을 확인해보세요. | 메인 홀은 꼼꼼히 살펴보셨나요? | 메인 홀 근처도 한번 살펴보세요. |
| Room5 (`room5`) | 화장실 앞쪽 | 아직 안 가보신 화장실 앞쪽을 확인해보세요. | 화장실 앞쪽은 꼼꼼히 살펴보셨나요? | 화장실 앞쪽도 한번 살펴보세요. |
| Room6 (`room6`) | 쓰레기통쪽 공간 | 아직 안 가보신 쓰레기통쪽 공간을 확인해보세요. | 쓰레기통쪽 공간은 꼼꼼히 살펴보셨나요? | 쓰레기통쪽도 한번 살펴보세요. |
| Room7 (`room7`) | 베란다 가는 복도 | 아직 안 가보신 베란다 가는 복도를 확인해보세요. | 베란다 가는 복도는 꼼꼼히 살펴보셨나요? | 베란다 가는 복도도 한번 살펴보세요. |
| Room8 (`room8`) | 413 강의실 | 아직 안 가보신 413 강의실을 확인해보세요. | 413 강의실은 꼼꼼히 살펴보셨나요? | 413 강의실 근처도 한번 살펴보세요. |

### 해석 시 주의사항

- 발화는 정해진 시간표대로 무조건 순차 재생되는 것이 아니라, 위 조건을 2초마다 다시 평가해 그 시점에 적합한 한 문장을 고르는 상태 기반 메커니즘이다.
- `minimumUtteranceGapSeconds`는 이전 **실제 재생 성공 시각**을 기준으로 한다. 억제되거나 클립 로드에 실패한 시도는 간격 기준을 갱신하지 않는다.
- 돌을 발견하면 최근 발견 증거가 AES 창 동안 gate를 통과시키고, 재방문 proxy와 zone vote를 초기화한다. 돌을 들고 있는 동안에는 검색 안내를 내보내지 않는다.
- FP1/FP2의 룸 ID는 음성 clip suffix를 결정하며, 실제 객체가 그 룸에 안전하게 배치되는지는 앞 절의 baked·supplemental·walkable-path 안전 로직이 별도로 담당한다.
