using Llm.Core.Data;
using Llm.Core.Plans;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plans;

/// <summary>
/// A chat's plan into the project the chat is put in: the plan is a goal of the project (of the same id, so the chat's plan
/// card finds it), its steps the goal's tasks (where each stands, who does it, when, what it waits for; done when it was
/// done), each the chat's. What the upgrade to 8.2 did for the plans of chats already in a project; Code Arena's working
/// plans stay its own.
/// </summary>
public static class PlanMove
{
    /// <summary>Moves the chat's plans into the project; how many.</summary>
    public static async Task<int> IntoProjectAsync(AppDbContext db, Guid chat, Guid project, CancellationToken ct)
    {
        var plans = await db.Plans.Include(p => p.Steps).Where(p => p.ConversationId == chat && p.Source == Plan.FromChat).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        if (plans.Count == 0)
        {
            return 0;
        }
        var position = await db.ProjectGoals.Where(g => g.ProjectId == project).MaxAsync(g => (int?)g.Position, ct) ?? -1;
        foreach (var plan in plans)
        {
            var steps = plan.Steps.OrderBy(s => s.Number).ToList();
            var allDone = steps.Count > 0 && steps.All(s => s.Status == PlanStep.Done);
            db.ProjectGoals.Add(new ProjectGoal
            {
                Id = plan.Id, ProjectId = project, Title = Cut(plan.Title is { Length: > 0 } t ? t : "A plan", 200), Description = plan.Goal is { } g ? Cut(g, 2000) : null,
                Status = allDone ? ProjectGoal.Done : steps.Any(s => s.Status is PlanStep.Doing or PlanStep.Done) ? ProjectGoal.Doing : ProjectGoal.Planned,
                DueOn = steps.Max(s => s.Due), DoneAt = allDone ? plan.UpdatedAt : null, Position = ++position, ChatId = chat, CreatedAt = plan.CreatedAt,
            });
            var ids = steps.ToDictionary(s => s.Number, s => s.Id);
            foreach (var s in steps)
            {
                var done = s.Status == PlanStep.Done;
                db.ProjectTasks.Add(new ProjectTask
                {
                    Id = s.Id, ProjectId = project, GoalId = plan.Id, Title = Cut(s.Title, 300), Notes = s.Notes is { } n ? Cut(n, 4000) : null, Owner = s.Owner,
                    Status = s.Status, Done = done, DoneAt = done ? s.CheckedAt ?? plan.UpdatedAt : null, StartOn = s.Start, DueOn = s.Due,
                    DependsOn = [.. s.DependsOn.Where(ids.ContainsKey).Select(d => ids[d])], CreatedById = plan.UserId, ChatId = chat, CreatedAt = plan.CreatedAt,
                });
            }
            db.Plans.Remove(plan);
        }
        await db.SaveChangesAsync(ct);
        return plans.Count;
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..most] : s;
}
