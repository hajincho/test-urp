# Hu Tao 이식 – 분석 요약 (재사용용)

작성: 2026-11-07. 이후 작업은 이 파일과 `hutao_rig_analysis.json`을 먼저 읽고, 프로젝트를 다시 전체 탐색하지 않는다.
상세 본 표(305개, 부모·길이·웨이트·메시)는 `hutao_rig_analysis.json`.

## 0. 작업 환경의 사실
- 이번 세션에는 **Blender MCP / Unity MCP 도구가 없었다**(`localMcpServers` 비어 있음, 기기 셸 없음). 파일 읽기/쓰기만 가능.
- 따라서 `.blend`는 Blender 없이 직접 파싱(zstd 해제 → 순수 Python 블록/DNA 파서)했고, Unity 컴파일·Play Mode 테스트는 **수행하지 못했다**.
- 원본 `.blend`, FBX, 기존 스크립트는 수정하지 않았다(읽기 전용).

## 1. 기존 캐릭터(Furina) 로직 – 코드로 확인한 것
| 질문 | 답 |
|---|---|
| 구현 방식 | 혼합. 몸 동작은 `UntitledCharacterMotion`이 만드는 **절차적 모션 스켈레톤**("Original Motion Skeleton", Bone_NNN)을 `BlenderCharacterRetarget`이 Furina 본에 리타게팅. 옷은 `SilkClothDynamics`(실시간 PBD), 땋은머리는 리타게팅 단계의 `UpdateBraids`(SmoothDamp 지연 추종 + 가속도 항, 변위 한도 약 2cm) 절차식. 키프레임 .anim 로코모션은 없음(프로젝트의 .anim은 물레방아·풍차 2개뿐). |
| 이동/가속/회전 영향 | 예. `SilkClothDynamics`가 허리 속도·가속도·각속도·각가속도를 직접 샘플링해 관성(`-a`), 원심/오일러 항, 속도 비례 공기저항에 반영. |
| 관절별 다른 파라미터 | 예. `BoneSettings` 11개 값이 체인 깊이(root→tip)에 따라 Lerp, 레이어(HB2/3/4)별 보정. |
| 부모→자식 전달 | `parentMotionInfluence`(부모 이동 이송), 각도 제약 + 곡률 유지(curvatureRetention), 길이 제약(PBD), 정/역방향 길이 패스. |
| 충돌 | 캡슐 근사 7개(몸통·허벅지·정강이·발)와 파티클 구 충돌, 체인 간 거리 쌍 분리, 초기 접촉 제외 목록. |
| 알고리즘 | 60 Hz 고정 서브스텝(최대 6), Verlet 유사 적분 + PBD 길이/각도 제약 + 스프링 + 비틀림 스프링. 실행 순서: 모션 100 → 리타게팅 195 → 물리 198 → 카메라 200. |
| 한계 | `HB` 접두어·Furina 본 이름("Hips","Chest","Left leg"…)에 하드코딩 → 다른 리그에 그대로 못 씀. 머리카락(땋은머리)은 PBD가 아니라 변위가 작게 제한된 SmoothDamp 지연 추종이라 충돌·체인 제약이 없음. |
| 규모 | Furina 시뮬레이션 본 113개(HB, 웨이트 있음) + 땋은머리 36관절(SmoothDamp 지연 추종) = **149**. |

## 2. 신규 .blend(Hu Tao, MMD 계열) 분석
- 본 **305개**, 전부 Blender에서 deform 플래그가 켜져 있으나 **스킨 웨이트가 실제로 있는 본은 188개**. 메시 오브젝트 21개(정점 약 7만, 버텍스 그룹 231 엔트리), Constraint 0, Action 0, 드라이버/NLA 없음.
- 계층: `全ての親 → センター → センター2 → グルーブ → グルーブ2 → 腰 → {下半身, 上半身}`. IK 6개는 웨이트 없음.
- **다리 웨이트는 D본(足D/ひざD/足首D/足先EX, 8개)에 있고 足/ひざ/足首는 웨이트 0** → D본이 원본 다리 회전을 1:1 따라가야 함(구현됨).
- 팔 비틀림 보조(腕捩/手捩 및 1~3) 16개 중 14개 웨이트 있음 → 현재는 rest 유지(비틀림 분배는 후속).
- 2차 체인 **155본 / 17체인**(웨이트 있는 본 117, 나머지 38은 `_end`·말단 무웨이트 본):

| 종류 | 체인 | 본 수 | 웨이트 본 | 위치(부모) |
|---|---|---|---|---|
| HairBack 후발3 | 좌우 각 1 | 34 | 30 | 頭, 체인당 17본(웨이트 15) |
| HairSide 측발2 | 좌우 각 1 | 16 | 12 | 頭 |
| HairFront 발1 | 좌우 각 1 | 14 | 10 | 頭 |
| Tassel 帽流苏 | 1 | 7 | 5 | 頭 |
| HatStrap 帽带 | 좌우 각 1 | 16 | 12 | 頭 |
| Belt 带 | 좌우 각 1 | 14 | 10 | 上半身 |
| Hem 后摆(코트 뒷자락) | 좌우 각 3열 | 54 | 38 | 下半身 |

- 머리카락 체인(웨이트 정점 수): 후발3 143→690→180(체인당 4,727정점), 측발2/발1/모자끈/띠는 48~170정점대.
- 텍스처는 3장(Face/Outfit/Hair, 2048², RGBA). 재질→텍스처: 发·目·帽·帽_饰=Hair, 新規·鞋袜·戒指·带1·神之眼AL·肌·服·裤=Outfit, 나머지=Face.
- FBX(`Hu tao.fbx`)는 `.blend`와 동일 본 구조(Model 330 = 305본 + 21메시 + 4)로 확인. Unity 입력은 FBX 사용(기존 Furina와 동일한 방식).

## 3. 설계 결정
- 알고리즘: **기존 애니메이션(절차 모션) + 다중 관절 PBD 혼합**. Furina `SilkClothDynamics`와 같은 설계(스프링+각도 제한+PBD 길이+곡률 유지+캡슐 충돌+체인 분리)를 새 컴포넌트 `HuTaoChainPhysics`로 이식(기존 클래스는 sealed·Furina 하드코딩이라 수정 대신 독립 구현).
- 새로 추가한 머리카락 품질 요소: 종류별 프로파일(뒷머리 길고 느슨 / 앞·옆머리 단단), 체인별 결정적 편차(좌우 비대칭), 머리·목·팔 캡슐 충돌 + 초기 접촉 제외, 인접 가닥 분리 쌍, 가닥 비틀림 스프링, 체인별 위상이 다른 느린 돌풍 노이즈(대기 중 미세 흔들림, `idleBreeze`로 0 가능), 끝으로 갈수록 자유도 증가(stiffness·limit·parent 영향 Lerp).
- 중복 제어 방지: `HuTaoRetarget`은 몸/손가락/다리/D본만, 체인 155본은 `HuTaoChainPhysics`만 쓴다.
- 새 스크립트는 독립 컴포넌트이며 기존 스크립트·프리팹·씬은 **수정하지 않음**. 전환기는 씬 로드 후 스스로 부트스트랩.

## 4. 생성/추가한 파일
- `Assets/Game/Scripts/ThirdPerson/HuTao/HuTaoChainPhysics.cs` – 2차 체인 PBD (런타임 활용률 리포트 `F9`)
- `Assets/Game/Scripts/ThirdPerson/HuTao/HuTaoRetarget.cs` – 모션 스켈레톤 → MMD 본 리타게팅(방향/크기/위치 자동 맞춤 포함)
- `Assets/Game/Scripts/ThirdPerson/HuTao/CharacterSwitcher.cs` – 1=Furina, 2=Hu Tao
- `Assets/Game/Editor/HuTaoImportSettings.cs` – FBX 임포트 설정 자동 적용
- `Assets/Game/Characters/HuTao/Resources/HuTao/{HuTao.fbx, Face.png, Hair.png, Outfit.png}`
- `Docs/HuTao/{analysis_summary.md, hutao_rig_analysis.json}`

## 5. 검증 상태(정직한 기록)
- 수행: `.blend` 구조/웨이트 파싱, FBX 재질·본 대응 확인, 기존 코드 정독, 새 코드 수기 검토.
- **미수행**: Unity 컴파일, Play Mode, 모든 동작 시나리오(Idle~전환), 성능 측정, Blender 내 메시 변형 테스트. 관절 활용률의 "실행 중 유의미 이동 본 수"는 Play Mode에서 `F9`로 측정해야 한다.

## 6. 후속(미착수)
파라미터 튜닝, 팔 비틀림 분배, 눈 깜빡임/모프, 발 IK, Furina 대비 비교 녹화, 성능 프로파일, 의상(후마) 관통 점검.

## 7. 2차 수정 (2026-11-07 01:00~)
- **다리 X자 꼬임**: 원인 = 발 목표를 원본 스켈레톤의 (좁은) 발 위치 + 발 로컬 오프셋으로 잡아, 골반이 넓은 Hu Tao 다리가 안쪽으로 모임. 수정 = 발 목표를 `Hu Tao 고관절 + 원본 고관절→발목 벡터 × 다리비 + rest 보정(root 공간)`으로, 무릎 굽힘 방향은 원본 무릎 평면 + 약한 전방 바이어스. Play에서 대기 자세 다리 교차 해소 확인(저해상도 육안).
- **키**: Hu Tao 모자 제외 키 = Furina 모자 제외 키. 비율(정점 기준): Furina 1.5634/다리 0.8062 = 1.93935(帽 본 가중 정점·DM 장식 제외, 정수리 기준), Hu Tao 18.921/9.915 = 1.90838(帽·帽_饰 메시 제외). 런타임 로그: `hatless height=1.384 (Furina hatless=1.384) motionScale=0.898`. 모션 스켈레톤 이동량(골반·보폭)도 0.898배로 축소.
- **뒷자락(Hem)**: 끝단 강성 4.5→3.6, 감쇠 5→3.8, 각도 한도 52→64°, 공기저항·가속 반응 상향, 달릴 때(지면속도 1.6~5 m/s 블렌드) 강성 ×0.5·한도 ×1.4, 속도² 비례 진행파 펄럭임 + 양력(`flutter`, 전역 `runFlutter`). 급정지 후 천천히 감쇠(runBlend 하강 2.2/s). 띠(Belt)도 약하게 적용.
- **눈 깜박임**: FBX/.blend 모두 셰이프 키 없음 → `HuTaoBlink`가 런타임에 "Blink" 블렌드셰이프 생성(白目 타원 기준, 윗눈꺼풀 面/面1/睫/二重 압축, 안구 白目/目/目星 접힘, 眉 제외). Furina와 동일하게 모션 스켈레톤 IdleBlink 가중치(또는 BlinkWeight)로 구동. 오프라인 렌더로 형상 검증, 게임 내 클로즈업 육안 확인은 미수행.
- Play 중 스크립트 재컴파일 시 물리 배열 불일치 예외 → `initialized` 비직렬화로 재초기화되게 수정.
- 측정: 활용률 155본 중 138 이동(웨이트 117 전부), layerPairs 14, 물리 1스텝 ~4.4ms(에디터) — 최적화 필요.

## 8. 3차 수정 (2026-11-07 01:25~)
- **Hu Tao 소스 변경**: 원본 모션 스켈레톤 대신 **Furina의 최종 포즈**(BlenderCharacterRetarget 적용 후)를 리타게팅. `HuTaoRetarget` 실행 순서 197. Hu Tao 표시 중에도 Furina는 활성 상태로 포즈만 계산(렌더러·SilkClothDynamics·Furina 머리 물리 off). → 대기 시 팔 벌림 해소(팔이 몸 옆), 걸을 때 발 교차 해소(정면 연속 캡처로 확인). motionScale=1.016(Hu Tao 다리/Furina 다리).
- **손 모양**: 손가락을 국소 변화량 대신 Furina 손가락 절대 방향으로 정렬(마지막 마디는 중간 마디 방향을 연장).
- **Furina 머리카락**: `FurinaHairRig`가 CF_2_L/R에 4개씩, 바보털 DM_5_1에 3개 힌지 추가(SetupCurlTips와 같은 웨이트 분할, 해당 본 웨이트만 재분배) → 머리 관절 67개(땋은머리 18×2 + 컬 힌지 5×2, 옆머리 2×2+4×2, 바보털 6+3)를 `HuTaoChainPhysics`(Furina 이름 모드, 캡슐 14개)로 시뮬레이션. 바보털 전용 프로파일(높은 강성·낮은 감쇠로 튕김, 중력 영향 작음).
- 기존 방식: 땋은머리는 SmoothDamp 지연 추종(변위 약 2cm 제한), CF는 원본 회전 복사, 바보털(DM 6관절)은 웨이트는 있었지만 아무도 움직이지 않았음.
- 체인 끝 본 중 웨이트가 있는 본에 가상 끝 파티클 추가 → Furina 머리 67/67 관절이 1° 이상 움직임(F9 측정). 물리 1스텝 Furina 약 2.0ms, Hu Tao 약 4.4ms(에디터).
- 기존 스크립트(BlenderCharacterRetarget 등)는 수정하지 않음. Furina 머리 본은 retarget이 먼저 쓰고 물리(198)가 덮어씀.
