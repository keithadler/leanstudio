/-- Doubling a number, the long way round. -/
def double (n : Nat) : Nat := n + n

theorem double_eq_two_mul (n : Nat) : double n = 2 * n := by
  unfold double
  omega

theorem and_swap (p q : Prop) (h : p ∧ q) : q ∧ p := by
  obtain ⟨hp, hq⟩ := h
  exact ⟨hq, hp⟩

/-- An assumption this project makes, not one Lean provides. -/
axiom em' (p : Prop) : p ∨ ¬p

theorem not_not_elim (p : Prop) (h : ¬¬p) : p := by
  cases em' p with
  | inl hp => exact hp
  | inr hnp => exact absurd hnp h

theorem unfinished (a b : Nat) : a * b = b * a := by
  sorry
