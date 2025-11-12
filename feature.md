# 新的 Integration 功能总结

## 🎯 主要 Integration 功能

### 1. **ML Intent Classification 集成** 
- **功能**: 将自然语言 prompt 自动分类为 intent 并提取 entities
- **端点**: `POST /orchestrator/classify`
- **支持方法**: `regex`, `gpt`, `auto`, `both`
- **集成位置**: `orchestrator/views.py` 中的 `classify_and_route` 函数

### 2. **真实 S3 数据集成**
- **功能**: 从 S3 获取真实的扫描数据和指标
- **CSV Lookup**: `lib/csv_lookup.py` - 通过 UUID 或 note_subject 查找患者扫描数据
- **Metrics Utils**: `lib/metrics_utils.py` - 从 S3 获取扫描指标（saccade, microsaccade, velocity, amplitude）
- **Video Utils**: `lib/video_utils.py` - 获取视频 URL（raw 和 annotated）

### 3. **新增 Agents**

#### **MetricsAgent** (`fetch_metrics`)
- 从 S3 的 processed CSV 中提取眼动指标
- 支持参数: `scan_id`, `mode`, `odos`, `scan`, `metric_type`
- 返回: saccade metrics, micro saccades, velocity, amplitude

#### **VideoAgent** (`fetch_video`)
- 提供存储在 S3 上的原始或标注视频的 URL
- 支持参数: `scan_id`, `mode`, `odos`, `scan`, `video_type` (raw/annotated)
- 自动从 CSV lookup 获取 file_name

#### **RecommendationAgent** (`generate_recommendations`)
- 基于指标生成临床建议和后续问题
- 规则基础的建议系统
- 返回: recommendations, follow_up_questions, suggested_tests, clinical_flags

#### **PatientOverviewAgent** (`patient_overview`)
- 提供全面的患者摘要
- 包括: demographics, visit history, aggregated metrics, clinical flags
- 支持 CSV lookup 和数据库查询两种模式
- 自动聚合多个扫描的指标

### 4. **数据层增强** (`data_layer.py`)
- **CSV Lookup 集成**: 支持通过 `note_subject` (如 "CLIGHT-META-001") 查找患者
- **真实 S3 数据访问**: 
  - `get_patient_scans_by_note_subject()` - 从 CSV 获取患者扫描
  - `get_scan_metadata()` - 获取扫描元数据
  - `get_patient_metric_trends()` - 获取患者指标趋势
- **混合模式**: 同时支持数据库查询和 CSV lookup

### 5. **Orchestrator 增强**
- **ML-enabled mode**: 支持通过 `prompt` 自动分类 intent
- **工具链执行**: 支持多个工具顺序执行，中间结果自动传递
- **结构化日志**: JSON 格式的事件日志（orchestrator_start, tool_done, orchestrator_done）

## 📋 新的 Intent 列表

| Intent | Agent | 功能 |
|--------|-------|------|
| `fetch_metrics` | MetricsAgent | 获取扫描指标 |
| `fetch_video` | VideoAgent | 获取视频 URL |
| `generate_recommendations` | RecommendationAgent | 生成临床建议 |
| `patient_overview` | PatientOverviewAgent | 患者概览 |
| `fetch_scan` | ScanAgent | 获取扫描（已增强） |
| `summarize_visit` | VisitAgent | 访问摘要（已增强） |

## 🔧 新的工具库

1. **`lib/csv_lookup.py`** (385 行)
   - `lookup_by_uuid()` - 通过 UUID 查找
   - `get_patient_scans_by_note_subject()` - 通过 note_subject 查找
   - CSV 数据缓存和解析

2. **`lib/metrics_utils.py`** (256 行)
   - `fetch_scan_metrics()` - 从 S3 获取指标
   - `aggregate_patient_metrics()` - 聚合患者指标
   - 支持多种指标类型: summary, saccade, microsaccade, velocity

3. **`lib/video_utils.py`** (170 行)
   - `get_video_urls()` - 获取视频 URL
   - 支持 raw 和 annotated 视频
   - 自动生成 presigned URLs

## 🚀 使用示例

### 1. ML Classification + Agent Execution
```bash
curl -X POST http://localhost:8000/orchestrator/run \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "demo-1",
    "prompt": "Show Patient A scan and get metrics",
    "method": "regex",
    "use_gpt": false,
    "entities": {"patient_id": "A"}
  }'
```

### 2. Fetch Metrics from S3
```bash
curl -X POST http://localhost:8000/orchestrator/run \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "demo-metrics",
    "intents": ["fetch_metrics"],
    "entities": {
      "scan_id": "9f4db82f-6134-435c-a886-51ef63578f73",
      "mode": 0,
      "odos": 0,
      "scan": 0
    }
  }'
```

### 3. Patient Overview with CSV Lookup
```bash
curl -X POST http://localhost:8000/orchestrator/run \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "demo-overview",
    "intents": ["patient_overview"],
    "entities": {
      "patient_id": "CLIGHT-META-001",
      "include_metrics": true,
      "include_visits": true
    }
  }'
```

### 4. Multi-tool Pipeline
```bash
curl -X POST http://localhost:8000/orchestrator/run \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "demo-pipeline",
    "intents": ["fetch_metrics", "generate_recommendations"],
    "entities": {
      "patient_id": "A",
      "scan_id": "9f4db82f-6134-435c-a886-51ef63578f73",
      "mode": 0,
      "odos": 0,
      "scan": 0
    }
  }'
```

## 📝 关键改进

1. **真实数据集成**: 不再使用 mock 数据，直接从 S3 获取
2. **CSV Lookup**: 支持通过 note_subject 查找患者（无需数据库 ID）
3. **指标聚合**: 自动聚合多个扫描的指标，生成趋势分析
4. **临床建议**: 基于实际指标生成规则基础的临床建议
5. **视频支持**: 获取原始和标注视频的 presigned URLs

