# FP1 이름없는 룸 6 Baked + Live Wall Veto + AprilTag 적용

## 현재 적용 방식

- FP1의 태그 기준, 방 기준 좌표, 객체 기본 배치는 검증된 Baked 공간을 사용한다.
- 평면도의 `이름없는 룸 6`은 기존 코드의 `room3`이다.
- Room UUID: `0d537c33-3e47-2606-3ea9-897c2bc9f1ce`
- Floor UUID: `62768480-a2ae-7bb5-3d77-e8de8355e2cb`
- 실제 벽의 `tagStandard41h12`, ID `0`, 검출 크기 `0.095 m` 태그로 Baked 공간 전체를 실제 공간에 강체 정렬한다.
- Quest의 현재 MRUK 스캔은 벽과 기둥 안전성에 대한 추가 veto로만 사용한다.

현재 스캔이 없거나 일부 벽 데이터를 제공하지 않아도 Baked 기준 좌표와 태그 인식은 유지된다. 현재 스캔 벽 정보가 있으면 Baked상 안전해도 실제 벽에 가까운 후보를 추가로 제외한다.

## 객체 배치 안전 규칙

- 타깃, 돌탑, incidental 객체의 floor-local 위치를 Baked floor transform으로 복원한다.
- Baked 벽에서 최소 `0.30 m`를 확보한 뒤 현재 스캔의 바닥 경계와 `WALL_FACE`, `INNER_WALL_FACE`, `INVISIBLE_WALL_FACE`로 다시 검사한다.
- 안전하지 않은 위치는 기존 완료 세션에서 추출한 FP1 보행 가능 경로 중 벽에 안전한 지점으로 이동한다.
- Baked 태그 위치와 yaw 보정을 최종 객체 위치에 동일하게 적용한다.
- 돌탑 회전에는 floor plane의 90도 pitch를 곱하지 않는다. 저장된 upright yaw와 태그 yaw만 적용한다.

## 빌드 전 확인

1. `MainTest_FP1` 씬을 연다.
2. `AAG/Validate FP1 AprilTag Scene`을 실행한다.
3. 검증이 통과하면 `AAG/Build FP1 Android APK`를 실행한다.

## 현장 실행

1. 이름없는 룸 6에서 ID 0 태그를 바라본다.
2. HUD가 `STABLE`이 될 때까지 유지한다.
3. 양쪽 thumbstick을 약 1.5초 눌러 정렬을 적용한다.
4. 정렬 성공 후 세션을 시작한다.
5. 첫 확인에서는 돌탑이 수직인지, 기둥과 벽 주변 돌이 30cm 이상 안쪽으로 이동했는지 확인한다.

현재 스캔은 기준 공간을 대체하지 않는다. Baked 전체를 다시 만들 필요 없이 실제 벽과 충돌하는 후보만 보조 검사에서 제외한다.
