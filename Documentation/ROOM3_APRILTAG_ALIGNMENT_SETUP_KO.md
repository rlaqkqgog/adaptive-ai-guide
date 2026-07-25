# Room3 AprilTag translation alignment

## Scene authoring

1. `Assets/Scenes/MainTest_FP1.unity`를 연다.
2. Scene 뷰의 `Gizmos`를 켠다.
3. `AAG_Room3_Tag_Alignment`를 선택하면 최신 FP1 Room3 export가 선으로 표시된다.
   - 청록색: Room3 벽
   - 노란색: 출입문 프레임
   - 회색: 바닥 경계
4. `MRUKAlignmentRoot/Room3_TagReference`를 선택한다.
5. Transform 원점을 실제 AprilTag의 **95 mm 검출 사각형 중심**에 대응하는 벽 위치로 옮긴다.
   전체 171 mm 인쇄판이나 QR 중심을 기준으로 삼지 않는다.
6. 회전은 태그의 앞면 로컬 `+Z`가 방 안을 향하게 둔다.
7. Inspector의 `Reference Placement Confirmed`를 마지막에 체크한다.

초기 Transform은 첨부 절차의 예시인 “문 프레임의 열린 쪽에서 0.70 m, 바닥에서
태그 중심 1.35 m”로만 배치되어 있다. 실제 설치 위치가 다르면 반드시 수정해야 한다.

## Runtime safety flow

- 검출 설정: `tagStandard41h12`, ID `0`, 검출 크기 `0.095 m`.
- 왼쪽 Passthrough Camera의 이미지 시각 pose로 카메라 좌표를 Unity world 좌표로 변환한다.
- 2초 동안 최소 12개 표본을 모으고 최대 흔들림이 4.5 cm 이하일 때만 Preview가 유효하다.
- 보정값은 `Detected World - Room3 Reference World`이다.
- 현재 현장 승인 씬은 `Horizontal Only = false`라 XYZ 평행이동을 사용한다.
- 현장 승인 씬은 `Preview Only = false`, `Allow Quest Controller Apply = true`이며
  양쪽 thumbstick을 1.5초 눌러 명시적으로 적용한다.
- 콘텐츠에는 태그 보정과 별도로 태그 기준 수평축의 고정 기준 `-0.65 m`에
  조정값 `+0.15 m`를 더한 최종 `-0.50 m`와
  `벽에서 방 안쪽 0.25 m` 미세조정이 더해진다. 높이 미세조정은 0이다.
- 빨간 큐브는 현실 태그 중심 확인용이라 이 콘텐츠 미세조정을 받지 않는다.
- 상세 AprilTag 정렬 HUD는 Apply가 성공하면 자동으로 숨고, `A`로 세션이 Running 상태에
  들어갈 때도 강제로 숨긴다. 정렬을 Reset하면 다시 나타난다.
- 보정은 `AagFixedSpaceOffset`을 통해 앱 소유 실험 콘텐츠에만 적용된다. MRUK, Room/Anchor,
  OVRCameraRig, TrackingSpace는 이동하지 않는다.
- 적용된 최신 태그 보정이 없거나 검출이 12초보다 오래됐으면 FP1 실험 시작은 차단된다.

현재 씬의 풀 경로:

```text
/AAG_Room3_Tag_Alignment/MRUKAlignmentRoot/Room3_TagReference/Room3_TagReference_RedCube_10cm
```

## Runtime HUD fields

- `Detected median`: 현실 AprilTag의 안정화된 Unity world 위치
- `MRUK reference`: 현재 Room3 floor anchor에서 복원한 기준 위치
- `Offset`: 적용 예정 평행이동
- `Jitter`: 2초 표본의 최대 위치 편차

40 m 초과 전체 오프셋, 0.5 m 초과 수직 오프셋, Room/floor UUID 변경, 미확정 기준점,
불안정한 태그 pose는 모두 fail-closed 처리된다.

세션마다 앱을 완전히 종료한 뒤 새로 실행하고, 태그 `STABLE` 확인 → 양쪽 thumbstick
1.5초 Apply → 빨간 큐브 일치 확인 → `A` Play 순서를 사용한다. Quest 로비 진입이나
앱 pause가 발생한 실행은 이어서 사용하지 않는다.

FP2 이식 전체 절차와 보류 중인 공간 변화 감시는
[`FP2_HYBRID_APRILTAG_MIGRATION_RUNBOOK_KO.md`](FP2_HYBRID_APRILTAG_MIGRATION_RUNBOOK_KO.md)를 참조한다.
