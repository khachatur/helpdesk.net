import { TextBox } from 'devextreme-react/text-box'

interface TextFieldProps {
  label: string
  value: string
  onChange: (value: string) => void
  type?: 'text' | 'email' | 'password'
  /** The browser autocomplete hint, such as "username" or "new-password". */
  autoComplete?: string
  error?: string
  hint?: string
  disabled?: boolean
}

/** A labelled DevExtreme text box that shows a validation message under the input. */
export function TextField({ label, value, onChange, type = 'text', autoComplete, error, hint, disabled }: TextFieldProps) {
  return (
    <div className="field">
      <TextBox
        label={label}
        labelMode="outside"
        stylingMode="outlined"
        mode={type}
        value={value}
        onValueChange={onChange}
        valueChangeEvent="input"
        disabled={disabled}
        inputAttr={{ autocomplete: autoComplete ?? 'off' }}
        isValid={!error}
        validationError={error ? { message: error } : undefined}
        validationMessageMode="always"
        validationMessagePosition="bottom"
      />
      {hint && !error && <div className="field-hint">{hint}</div>}
    </div>
  )
}
