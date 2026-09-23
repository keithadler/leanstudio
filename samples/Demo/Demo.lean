theorem add_comm' (a b : Nat) : a + b = b + a := by
  induction a with
  | zero => simp
  | succ n ih =>
    rw [Nat.succ_add]
    omega

example (p q : Prop) (hp : p) (hq : q) : p ∧ q := by
  constructor
  · exact hp
  · exact hq

theorem bad : 1 = 2 := by sorry
