using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>Факты каталога об одной версии действия, на которую ссылается карта.</summary>
public sealed record WorkflowActionFact(
    string Module,
    string Action,
    int Version,
    bool Found,
    bool Enabled,
    IReadOnlyList<string> RequiredPolicy,
    IReadOnlyList<string> Outcomes,
    string IdempotencyMode,
    string IdempotencyScope);

/// <summary>Всё авторитетное состояние, нужное валидатору карты: каталог и права исполнителя.</summary>
public sealed record WorkflowPublicationFacts(
    IReadOnlyList<string> WorkerScopes,
    IReadOnlyList<WorkflowActionFact> Actions)
{
    public WorkflowActionFact? Find(string module, string action, int version)
        => Actions.FirstOrDefault(item =>
            item.Module == module && item.Action == action && item.Version == version);

    /// <summary>Читает факты из конверта функции workflow.publication_facts.</summary>
    public static WorkflowPublicationFacts FromResult(JsonObject result)
    {
        var scopes = result["workerPrincipal"]?["scopes"] is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
            : [];

        var actions = new List<WorkflowActionFact>();
        if (result["actions"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                actions.Add(new WorkflowActionFact(
                    item["module"]?.GetValue<string>() ?? string.Empty,
                    item["action"]?.GetValue<string>() ?? string.Empty,
                    item["version"] is { } version && int.TryParse(version.ToString(), out var parsed) ? parsed : 0,
                    item["found"]?.GetValue<bool>() ?? false,
                    item["enabled"]?.GetValue<bool>() ?? false,
                    ReadStrings(item["requiredPolicy"]),
                    ReadStrings(item["outcomes"]),
                    item["idempotencyMode"]?.GetValue<string>() ?? "none",
                    item["idempotencyScope"]?.GetValue<string>() ?? "none"));
            }
        }

        return new WorkflowPublicationFacts(scopes, actions);
    }

    private static string[] ReadStrings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
            : [];
}

/// <summary>
/// Семантическая проверка карты процесса поверх JSON Schema.
///
/// Схема доказывает форму документа, но не его смысл: она не знает ни про достижимость шагов, ни
/// про существующие действия, ни про полноту покрытия исходов. Всё это проверяется здесь — ДО
/// публикации, потому что опубликованная версия неизменяема и «починить» её потом нельзя.
/// </summary>
public static class WorkflowMapValidator
{
    /// <summary>Возвращает список нарушений. Пустой список означает «карта пригодна к публикации».</summary>
    public static IReadOnlyList<string> Validate(WorkflowMap map, WorkflowPublicationFacts facts)
    {
        var violations = new List<string>();

        // 1. Уникальность ключей шагов. Всё остальное опирается на индекс по ключу, поэтому при
        //    дубликате дальше идти бессмысленно.
        var duplicates = map.Steps
            .GroupBy(step => step.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            violations.Add($"duplicate step keys: {string.Join(", ", duplicates)}");
            return violations;
        }

        var steps = map.Steps.ToDictionary(step => step.Key, StringComparer.Ordinal);

        // 2. Один существующий start_step.
        if (!steps.ContainsKey(map.StartStep))
        {
            violations.Add($"start_step '{map.StartStep}' is not declared by the map");
        }

        // 3. Хотя бы один end.
        if (!map.Steps.Any(step => step.Type == WorkflowStepType.End))
        {
            violations.Add("the map declares no end step");
        }

        // 4. Переходы ссылаются на существующие шаги.
        foreach (var transition in map.Transitions)
        {
            if (!steps.ContainsKey(transition.From))
            {
                violations.Add($"transition source '{transition.From}' is not declared by the map");
            }

            if (!steps.ContainsKey(transition.To))
            {
                violations.Add($"transition target '{transition.To}' is not declared by the map");
            }
        }

        // Повторяющийся signal_type среди шагов ожидания НЕ запрещается. Соблазн был: два таких
        // шага выглядят двусмысленно. Но двусмысленности нет — граф ацикличен, за один проход
        // процесс входит либо в один шаг (взаимоисключающие ветки), либо в оба по очереди
        // (последовательные), а накопленные сигналы применяются в порядке получения. Запрет
        // отклонял бы карту, валидную по контракту курса, а лишняя строгость валидатора — это
        // такое же нарушение контракта, как и недостающая проверка.

        // 5. Контракт действия каждой automatic-задачи: существование, включённость, точное
        //    совпадение policy, достаточность server-side scopes исполнителя и идемпотентность.
        var stepOutcomes = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var step in map.Steps)
        {
            var actionOutcomes = Array.Empty<string>();
            if (step is { Type: WorkflowStepType.Automatic, Task: { } task })
            {
                violations.AddRange(ValidateTask(step.Key, task, facts, out actionOutcomes));
            }

            stepOutcomes[step.Key] = step.DeclaredOutcomes(actionOutcomes);
        }

        // 6. Ровно один переход на каждый объявленный исход и ни одного лишнего перехода.
        violations.AddRange(ValidateOutcomeCoverage(map, steps, stepOutcomes));

        if (violations.Count > 0)
        {
            return violations;
        }

        // 7. Достижимость, ацикличность и отсутствие тупиков считаются только на связном графе,
        //    иначе диагностика превращается в шум от уже найденных ошибок.
        violations.AddRange(ValidateGraph(map, steps));

        return violations;
    }

    private static IEnumerable<string> ValidateTask(
        string stepKey,
        WorkflowTask task,
        WorkflowPublicationFacts facts,
        out string[] actionOutcomes)
    {
        var violations = new List<string>();
        actionOutcomes = [];

        var fact = facts.Find(task.Module, task.Action, task.ActionVersion);
        if (fact is null || !fact.Found)
        {
            violations.Add($"step '{stepKey}' references an unpublished action {task.RouteKey} version {task.ActionVersion}");
        }
        else if (!fact.Enabled)
        {
            violations.Add($"step '{stepKey}' references a disabled action {task.RouteKey} version {task.ActionVersion}");
        }
        else
        {
            actionOutcomes = fact.Outcomes.ToArray();

            // Policy карты — это ЗАКРЕПЛЁННЫЙ контракт, а не подсказка: множества обязаны совпасть
            // точно, иначе после смены прав действия карта продолжала бы утверждать неправду.
            var declared = new HashSet<string>(task.RequiredPolicy, StringComparer.Ordinal);
            var actual = new HashSet<string>(fact.RequiredPolicy, StringComparer.Ordinal);
            if (!declared.SetEquals(actual))
            {
                violations.Add(
                    $"step '{stepKey}' declares required_policy [{string.Join(", ", declared.Order(StringComparer.Ordinal))}] " +
                    $"while the action declares [{string.Join(", ", actual.Order(StringComparer.Ordinal))}]");
            }

            if (actionOutcomes.Length == 0)
            {
                violations.Add($"step '{stepKey}' references an action without declared outcomes");
            }

            // Доставка задания — at-least-once, и «ровно один предметный эффект» держится не
            // движком, а ключом идемпотентности действия. Действие без идемпотентности при
            // первом же перехвате истёкшей аренды создаст второй эффект, поэтому связать с ним
            // automatic-шаг нельзя вообще: это отказ на публикации, а не сбой в рантайме.
            if (fact.IdempotencyMode == "none")
            {
                violations.Add(
                    $"step '{stepKey}' binds an automatic task to action {task.RouteKey} " +
                    $"version {task.ActionVersion}, which declares idempotency_mode 'none'; " +
                    "at-least-once delivery would create a second domain effect on reclaim");
            }
        }

        var missingScopes = task.RequiredPolicy
            .Where(scope => !facts.WorkerScopes.Contains(scope, StringComparer.Ordinal))
            .ToArray();
        if (missingScopes.Length > 0)
        {
            violations.Add(
                $"step '{stepKey}' requires policy [{string.Join(", ", missingScopes)}] " +
                "which the server-side workflow-worker principal does not hold");
        }

        if (task.DelaysMs.Count != task.MaxAttempts - 1)
        {
            violations.Add(
                $"step '{stepKey}' declares {task.DelaysMs.Count} retry delays for max_attempts {task.MaxAttempts}; " +
                $"exactly {task.MaxAttempts - 1} are required");
        }

        violations.AddRange(ValidateMapping(stepKey, task));
        return violations;
    }

    private static IEnumerable<string> ValidateMapping(string stepKey, WorkflowTask task)
    {
        var violations = new List<string>();
        var targets = new List<(string Pointer, string[] Tokens)>();

        // Константы задают исходный payload, поэтому их ключи участвуют в проверке пересечений
        // наравне с целевыми указателями mapping.
        foreach (var constant in task.InputConstants)
        {
            targets.Add(("/" + constant.Key.Replace("~", "~0").Replace("/", "~1"), [constant.Key]));
        }

        foreach (var pair in task.InputMapping.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!JsonPointer.TryParse(pair.Key, out var target) || target.Length == 0)
            {
                violations.Add($"step '{stepKey}' has an invalid target pointer '{pair.Key}'");
                continue;
            }

            if (!JsonPointer.TryParse(pair.Value, out var source) || source.Length == 0)
            {
                violations.Add($"step '{stepKey}' has an invalid source pointer '{pair.Value}'");
                continue;
            }

            targets.Add((pair.Key, target));
        }

        for (var left = 0; left < targets.Count; left++)
        {
            for (var right = left + 1; right < targets.Count; right++)
            {
                if (JsonPointer.Overlaps(targets[left].Tokens, targets[right].Tokens))
                {
                    violations.Add(
                        $"step '{stepKey}' maps overlapping payload targets " +
                        $"'{targets[left].Pointer}' and '{targets[right].Pointer}'");
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> ValidateOutcomeCoverage(
        WorkflowMap map,
        IReadOnlyDictionary<string, WorkflowStep> steps,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> stepOutcomes)
    {
        var violations = new List<string>();

        var byStep = map.Transitions
            .Where(transition => steps.ContainsKey(transition.From))
            .GroupBy(transition => transition.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var step in map.Steps)
        {
            var transitions = byStep.TryGetValue(step.Key, out var value) ? value : [];

            if (step.Type == WorkflowStepType.End)
            {
                if (transitions.Length > 0)
                {
                    violations.Add($"end step '{step.Key}' must not declare outgoing transitions");
                }

                continue;
            }

            var declared = stepOutcomes[step.Key];
            foreach (var outcome in declared)
            {
                var matching = transitions.Count(transition => transition.Outcome == outcome);
                if (matching == 0)
                {
                    violations.Add($"step '{step.Key}' has no transition for outcome '{outcome}'");
                }
                else if (matching > 1)
                {
                    violations.Add($"step '{step.Key}' has {matching} transitions for outcome '{outcome}'");
                }
            }

            foreach (var transition in transitions)
            {
                if (!declared.Contains(transition.Outcome))
                {
                    violations.Add(
                        $"step '{step.Key}' has a transition for outcome '{transition.Outcome}' " +
                        "which the step cannot produce");
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> ValidateGraph(
        WorkflowMap map,
        IReadOnlyDictionary<string, WorkflowStep> steps)
    {
        var violations = new List<string>();
        var edges = map.Transitions
            .GroupBy(transition => transition.From, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(transition => transition.To).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        // Достижимость от start_step.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(map.StartStep);
        reachable.Add(map.StartStep);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in edges.TryGetValue(current, out var targets) ? targets : [])
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        var unreachable = steps.Keys.Where(key => !reachable.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (unreachable.Length > 0)
        {
            violations.Add($"unreachable steps: {string.Join(", ", unreachable)}");
        }

        // Ацикличность: цикл в обязательной части сделал бы процесс незавершимым.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        if (steps.Keys.Any(key => HasCycle(key, edges, state)))
        {
            violations.Add("the map contains a cycle");
            return violations;
        }

        // Тупики: из достижимого не-end шага обязан существовать путь до end.
        var terminating = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in reachable.Where(steps.ContainsKey).Order(StringComparer.Ordinal))
        {
            if (!Terminates(key, steps, edges, terminating))
            {
                violations.Add($"step '{key}' has no path to an end step");
            }
        }

        return violations;
    }

    private static bool HasCycle(
        string key,
        IReadOnlyDictionary<string, string[]> edges,
        Dictionary<string, int> state)
    {
        if (state.TryGetValue(key, out var mark))
        {
            return mark == 1;
        }

        state[key] = 1;
        foreach (var next in edges.TryGetValue(key, out var targets) ? targets : [])
        {
            if (HasCycle(next, edges, state))
            {
                return true;
            }
        }

        state[key] = 2;
        return false;
    }

    private static bool Terminates(
        string key,
        IReadOnlyDictionary<string, WorkflowStep> steps,
        IReadOnlyDictionary<string, string[]> edges,
        Dictionary<string, bool> memo)
    {
        if (memo.TryGetValue(key, out var known))
        {
            return known;
        }

        if (!steps.TryGetValue(key, out var step))
        {
            memo[key] = false;
            return false;
        }

        if (step.Type == WorkflowStepType.End)
        {
            memo[key] = true;
            return true;
        }

        // Граф уже доказан ациклическим, поэтому рекурсия завершается без дополнительной защиты.
        memo[key] = false;
        var result = (edges.TryGetValue(key, out var targets) ? targets : [])
            .Any(next => Terminates(next, steps, edges, memo));
        memo[key] = result;
        return result;
    }
}
