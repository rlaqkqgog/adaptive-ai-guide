# FP2 공간 설정 및 빌드 절차

하이브리드 AprilTag 생성, 기준점 배치, Quest 검증, 세션 SOP와 공간 변화 감시까지의
전체 이식 절차는 [`FP2_HYBRID_APRILTAG_MIGRATION_RUNBOOK_KO.md`](FP2_HYBRID_APRILTAG_MIGRATION_RUNBOOK_KO.md)를 따른다.

## 보호된 FP1 기준점

- 안정 체크포인트 커밋: `ab1fcaf`
- 복구 태그: `fp1-stable-20260724`
- FP2 작업 브랜치: `codex/fp2-space-split`
- FP1 Build Profile은 `MainTest_FP1.unity`만 빌드한다.

FP1의 기존 Quest 저장 경로와 PlayerPrefs 키는 호환성을 위해 바꾸지 않는다.

## 공간별 파일

| 항목 | FP1 | FP2 |
|---|---|---|
| 메인 씬 | `Assets/Scenes/MainTest_FP1.unity` | `Assets/Scenes/MainTest_FP2.unity` |
| 설정 | `Assets/Experiment/FP1ExperimentConfig.asset` | `Assets/Experiment/FP2ExperimentConfig.asset` |
| Build Profile | `FP1_BuildProfile` | `FP2_BuildProfile` |
| 세션 로그 | `FP1Logs` | `FP2Logs` |
| 수동 앵커 manifest | `AagManualAnchorSets/fp1_manual_anchor_sets.json` | `AagManualAnchorSets/FP2/fp2_manual_anchor_sets.json` |
| Room-local 배치 | `fp1_floor_anchor_local_placements.json` | `fp2_floor_anchor_local_placements.json` |
| PlayerPrefs UUID | `numUuids`, `uuidN` | `AAG.FP2.numUuids`, `AAG.FP2.uuid.N` |

FP2는 모델과 공통 프리팹을 재사용하지만 UUID, 방 매핑, 앵커 manifest와 로그는 공유하지 않는다.

## FP2 Room UUID 등록

1. Unity 메뉴 `AAG > Build FP2 Room UUID Scanner APK`로 스캐너를 빌드한다.
2. FP2 공간에서 Meta Quest Space Setup을 완료하고 스캐너를 실행한다.
3. Quest의 `AagRoomExports/FP2`에서 `fp2_room_coordinates_*.json`을 가져온다.
4. 확인된 각 Room UUID를 `FP2ExperimentConfig.asset`의 `rooms`에 `roomId`, `displayName`, `tieOrder`와 함께 입력한다.
5. `startRoomId`가 실제 시작 방의 `roomId`인지 확인한다.

FP2 설정의 `rooms`가 비어 있는 동안 공간 검증은 의도적으로 실패한다. FP1 Room UUID로 대체하거나 우회하지 않는다.

## FP2 객체 UUID 등록

1. `MainTest_FP2.unity`의 `SpatialAnchorManager`에서 등록용 빌드에 한해서 `Enable Manual Set Authoring`을 켠다.
2. `FP2_BuildProfile`을 활성화해 빌드한다.
3. `FP2-S1`, `FP2-S2`, `FP2-S3`, 고정 타워, incidental 객체를 새 공간에서 저장한다.
4. 생성된 FP2 manifest를 반드시 외부로 export하고 백업한다.
5. 참가자용 FP2 빌드 전에는 `Enable Manual Set Authoring`을 다시 끈다.

FP2는 공통 Resources 프리팹 폴더 `FP1-S1`~`FP1-S3`를 재사용하도록 `assetSetIds`가 설정되어 있다. 이 배열은 시각 자산만 공유하며 UUID를 공유하지 않는다.

## 참가자용 빌드

1. `File > Build Profiles`에서 대상 프로필을 명시적으로 활성화한다.
2. FP1은 `FP1_BuildProfile`, FP2는 `FP2_BuildProfile`을 선택한다.
3. 활성 씬과 설정 에셋의 `spaceId`가 일치하는지 확인한다.
4. 프로필별 APK 이름을 구분해 보관한다.

FP1과 FP2를 Quest에 동시에 설치해야 할 때만 Android application identifier도 분리한다. application identifier를 바꾸면 앱의 persistent data가 분리되므로, 앵커 등록과 참가자 빌드가 같은 identifier를 사용하는지 먼저 확인해야 한다.
