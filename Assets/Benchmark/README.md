# Renderer Instrumentation + BenchmarkRunner 통합

## 동작 구조

1. 씬 로드 후 `BenchmarkRunner`가 RMI/BRG 렌더러 참조를 한 번 검색한다.
2. 요청 Case에 맞는 렌더러만 활성화하고 반대 렌더러는 비활성화한다.
3. 렌더러는 현재 프레임의 구조 계측값만 `CurrentStats`와 공통 인터페이스로 제공한다.
4. Runner는 `FrameSample`과 구조 계측 샘플을 동일 프레임 순서로 메모리에 저장한다.
5. 측정 종료 후 `BenchmarkResultWriter`가 한 번에 CSV를 기록한다.

렌더러 내부에는 파일 I/O가 없다.

## 프레임 CSV 추가 열

- `renderer_stats_valid`
- `renderer_source_frame`
- `renderer_case`
- `renderer_submission_mode`
- `renderer_instance_count`
- `renderer_active_submesh_count`
- `renderer_active_material_count`
- `renderer_commands_per_submesh`
- `renderer_submitted_command_count`
- `renderer_api_call_count`
- `renderer_uploaded_bytes_per_frame`
- `rmi_compute_dispatch_count`
- `brg_batch_count`
- `brg_draw_range_count`
- `brg_batch_draw_command_count`

## Inspector 설정

`Tools > Swarm Benchmark`에서:
- `원거리 투사체 렌더링`
- `프레임별 CSV 저장`
- `렌더러 구조 계측 포함`

을 각각 선택할 수 있다.

구조 계측을 꺼도 Runner의 Case 전환 기능은 유지된다.

`원거리 투사체 렌더링`을 끄면 전투 판정과 사격 애니메이션은 그대로 실행하고,
성능 측정에서 투사체 GameObject 생성 및 렌더링 비용만 제외한다.
