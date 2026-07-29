# 공간 오프셋 진단 및 고정 보정

## 목적

이 도구는 서로 멀리 떨어진 실제 기준점들을 Quest 컨트롤러 팁으로 측정하여 다음 중 무엇인지 구분한다.

- `FixedTranslation`: 모든 지점이 하나의 동일한 X/Z 평행이동으로 설명됨
- `YawAndTranslation`: 평행이동만으로는 맞지 않고 Yaw 회전까지 포함해야 함
- `Inconsistent`: 방마다 차이가 달라 고정 오프셋으로 해결할 수 없음
- `InsufficientData`: 측정점이 3개 미만이거나 측정점 사이 거리가 너무 가까움

보정 방향은 항상 다음과 같다.

```text
correction = 실제 기준점을 터치했을 때의 probe 월드 위치 - 가상 기준점 월드 위치
```

## 진단 씬 설정

1. `ExperimentMain` GameObject에 `AagSpaceOffsetDiagnostic`을 추가한다. `ExperimentMain`이 런타임에 자동 생성하기도 하지만, 기준점 설정을 저장하려면 에디터에서 직접 추가해야 한다.
2. 빈 GameObject `SpaceOffsetReferencePoints`를 만들고, 그 아래에 방별 기준점 Transform을 만든다. 예: `room1_door_left`, `room4_door_left`, `room8_door_left`.
3. 각 Transform을 저장된 가상 좌표계에서 해당 물리 랜드마크가 있어야 하는 위치에 둔다.
4. `Reference Points Root`에 `SpaceOffsetReferencePoints`를 지정한다. 직접 목록을 쓰는 경우에는 `Reference Points`에 각 Transform과 고유 ID를 지정한다.
5. `Probe Transform`에는 `RightControllerAnchor` 또는 컨트롤러 끝에 만든 전용 probe Transform을 지정한다. 컨트롤러 원점과 실제 접촉 지점이 다르면 `Probe Local Offset`을 조정한다.
6. 서로 다른 방에서 최소 3개, 가능하면 5개 이상의 기준점을 사용한다. 기준점 전체 폭은 최소 2m 이상이어야 한다.

## Quest에서 측정

1. 실험 세션을 시작하기 전 `Measurement Mode Enabled`를 켠 빌드를 실행한다.
2. 표시된 순서대로 컨트롤러 probe를 실제 랜드마크에 고정한다.
3. 오른쪽 검지 트리거를 누른다. 기본값으로 0.35초 동안 위치를 평균낸다.
4. 다음 방으로 이동하여 반복한다.
5. 잘못 측정했으면 오른쪽 `B`, 수동 내보내기는 오른쪽 `A`를 누른다.

각 측정 뒤 JSON과 CSV가 다음 위치에 기록된다.

```text
Application.persistentDataPath/AagSpaceDiagnostics/
```

Logcat에서는 `[AAG Offset Diagnostic]`으로 검색할 수 있다.

## 판정 기준

기본값은 X/Z 평면만 사용한다.

- 최소 표본: 3
- 최소 기준점 폭: 2m
- 평행이동 최대 잔차 허용값: 0.10m
- 회전 증거: 2도 이상
- Yaw 모델 채택: 평행이동 RMS의 65% 이하로 감소하고 최대 잔차도 0.10m 이하

`FixedTranslation`이 나온 경우에도 `translationMaxResidualMeters`를 확인한다. 논문 실험에서는 여러 앱 재실행과 헤드셋 재부팅 뒤 동일 측정을 반복하여 보정 벡터의 재현성을 확인한다.

## 고정 오프셋 적용

`Assets/Experiment/FP1ExperimentConfig.asset`에서 다음을 설정한다.

1. `Fiducial Marker Alignment Enabled`를 끈다.
2. `Fixed Space Offset Enabled`를 켠다.
3. 진단 JSON의 `recommendedTranslation`을 `Fixed Space Offset Meters`에 입력한다.
4. 바닥 높이를 MRUK에 맡기려면 `Fixed Space Offset Horizontal Only`를 켠다.

고정 보정과 QR 구역 보정이 동시에 켜져 있으면 이중 보정을 막기 위해 세션 시작이 차단된다.

세션 로딩 시 `ExperimentMain`은 다음 앱 소유 콘텐츠만 `AagFixedSpaceOffset`에 등록한다.

- 실험 대상 돌
- 고정 탑
- incidental object

`OVRCameraRig`, `TrackingSpace`, `MRUK`, `MRUKRoom`, `MRUKAnchor` 자체는 이동하지 않는다. 보정은 기준 위치에 대한 절대 대입 방식이므로 같은 값을 여러 번 적용해도 누적되지 않는다.

## 판정별 조치

- `FixedTranslation`: 고정 오프셋 사용 가능
- `YawAndTranslation`: 현재 구현의 고정 평행이동을 사용하지 말고 QR/rigid alignment 사용
- `Inconsistent`: 방 UUID 선택, MRUK room-local frame, Space Setup 재구성 또는 구역별 정렬 문제를 조사
- `InsufficientData`: 더 멀리 떨어진 방에서 기준점을 추가 측정
